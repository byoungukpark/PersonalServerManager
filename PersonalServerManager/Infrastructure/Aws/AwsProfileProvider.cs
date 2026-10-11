using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Services;

namespace PersonalServerManager.Infrastructure.Aws;

/// <summary>AWS CLI가 사용하는 공유 credentials/config 파일에서 프로필을 읽는다.</summary>
public sealed class AwsProfileProvider : IAwsProfileProvider
{
    public async Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run<IReadOnlyList<string>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sharedFile = OpenSharedFile();
                var names = sharedFile.ListProfileNames()
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                return names;
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.Other,
                "로컬 AWS 프로필을 읽지 못했습니다. AWS CLI 공유 설정 파일을 확인하세요.");
        }
    }

    /// <summary>목록과 동일한 공유 파일에서 후보 자격 증명을 해석한다.</summary>
    internal async Task<ResolvedAwsProfile> ResolveAsync(string profileName, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sharedFile = OpenSharedFile();
                if (!sharedFile.TryGetProfile(profileName, out var profile))
                {
                    throw new AwsConnectionException(
                        AwsConnectionFailureKind.ProfileMissing,
                        "선택한 AWS 프로필을 공유 설정 파일에서 찾을 수 없습니다. 목록을 다시 읽어 주세요.");
                }

                if (!string.IsNullOrEmpty(profile.Options.SsoAccountId) ||
                    !string.IsNullOrEmpty(profile.Options.SsoSession) ||
                    !string.IsNullOrEmpty(profile.Options.SsoStartUrl) ||
                    !string.IsNullOrEmpty(profile.Options.LoginSession))
                {
                    throw new AwsConnectionException(
                        AwsConnectionFailureKind.UnsupportedProfile,
                        "이 프로필의 인증 방식은 현재 지원하지 않습니다. AWS CLI의 다른 프로필을 선택하세요.");
                }

                if (!AWSCredentialsFactory.TryGetAWSCredentials(profile, sharedFile, out var credentials))
                {
                    throw new AwsConnectionException(
                        AwsConnectionFailureKind.Authentication,
                        "선택한 AWS 프로필의 자격 증명을 사용할 수 없습니다. 로컬 프로필 설정을 확인하세요.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                // 연결 화면에는 리전 선택이 없다. STS 호출은 프로필 리전 또는 서울 엔드포인트를 쓴다.
                return new ResolvedAwsProfile(credentials, profile.Region ?? RegionEndpoint.APNortheast2);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AwsConnectionException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.Authentication,
                "선택한 AWS 프로필의 자격 증명을 읽지 못했습니다. 로컬 프로필 설정을 확인하세요.");
        }
    }

    private static SharedCredentialsFile OpenSharedFile() =>
        new(SharedCredentialsFile.DefaultFilePath);
}

internal sealed record ResolvedAwsProfile(AWSCredentials Credentials, RegionEndpoint Region);
