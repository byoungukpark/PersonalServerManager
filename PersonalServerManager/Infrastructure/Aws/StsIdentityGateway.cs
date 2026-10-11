using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Models;
using PersonalServerManager.Application.Services;

namespace PersonalServerManager.Infrastructure.Aws;

/// <summary>선택된 로컬 프로필로만 STS GetCallerIdentity를 호출한다.</summary>
public sealed class StsIdentityGateway(AwsProfileProvider profiles) : IStsIdentityGateway
{
    public async Task<AwsIdentity> GetCallerIdentityAsync(string profileName, CancellationToken cancellationToken)
    {
        var candidate = await profiles.ResolveAsync(profileName, cancellationToken);

        try
        {
            using var client = new AmazonSecurityTokenServiceClient(candidate.Credentials, candidate.Region);
            var response = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
            if (response.Account is not { Length: 12 } account ||
                !account.All(char.IsAsciiDigit) ||
                string.IsNullOrWhiteSpace(response.Arn))
            {
                throw new AwsConnectionException(
                    AwsConnectionFailureKind.Authentication,
                    "AWS 인증 응답의 계정 ID 또는 Caller ARN이 올바르지 않습니다. 다시 시도하세요.");
            }

            return new AwsIdentity(account, response.Arn);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AwsConnectionException)
        {
            throw;
        }
        catch (AmazonServiceException exception)
        {
            throw MapServiceError(exception);
        }
        catch (AmazonClientException exception) when (
            exception.InnerException is HttpRequestException or SocketException or IOException)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.Network,
                "AWS STS에 연결하지 못했습니다. 네트워크 상태를 확인하세요.");
        }
        catch (AmazonClientException)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.Other,
                "AWS STS 요청을 처리하지 못했습니다. 네트워크와 로컬 프로필 설정을 확인하세요.");
        }
        catch (Exception)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.Other,
                "AWS 연결 확인 중 오류가 발생했습니다. 잠시 후 다시 시도하세요.");
        }
    }

    private static AwsConnectionException MapServiceError(AmazonServiceException exception)
    {
        var code = exception.ErrorCode ?? string.Empty;
        if (code is "ExpiredToken" or "ExpiredTokenException" or "RequestExpired")
        {
            return new AwsConnectionException(
                AwsConnectionFailureKind.ExpiredSession,
                "AWS 인증 세션이 만료되었습니다. 로컬 자격 증명을 갱신한 뒤 다시 연결하세요.");
        }

        if (code is "InvalidClientTokenId" or "UnrecognizedClientException"
            or "SignatureDoesNotMatch" or "InvalidSignatureException" or "AuthFailure")
        {
            return new AwsConnectionException(
                AwsConnectionFailureKind.Authentication,
                "AWS 자격 증명을 인증하지 못했습니다. 선택한 프로필을 확인하세요.");
        }

        if (code is "AccessDenied" or "AccessDeniedException")
        {
            return new AwsConnectionException(
                AwsConnectionFailureKind.AccessDenied,
                "AWS STS 요청이 거부되었습니다. 프로필의 권한과 정책을 확인하세요.");
        }

        return new AwsConnectionException(
            AwsConnectionFailureKind.Other,
            "AWS STS 인증 확인에 실패했습니다. 잠시 후 다시 시도하세요.");
    }
}
