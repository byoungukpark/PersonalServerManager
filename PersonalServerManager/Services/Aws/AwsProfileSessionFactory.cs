using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.RDS;
using Amazon.RDS.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

/// <summary>STS 검증과 AWS 읽기 요청이 동일한 프로필 자격 증명을 사용하도록 세션을 만든다.</summary>
public sealed class AwsProfileSessionFactory : IAwsProfileSessionFactory
{
    private readonly AwsProfileProvider _profiles;

    public AwsProfileSessionFactory(AwsProfileProvider profiles)
    {
        _profiles = profiles;
    }

    public async Task<IAwsProfileSession> CreateAsync(string profileName, CancellationToken cancellationToken)
    {
        var resolved = await _profiles.ResolveAsync(profileName, cancellationToken);
        return new AwsProfileSession(resolved.Credentials, resolved.Region);
    }
}

internal sealed partial class AwsProfileSession : IAwsProfileSession, IAwsResourceControlSession, IAwsCloudWatchSession
{
    private readonly AWSCredentials _credentials;
    private readonly RegionEndpoint _defaultRegion;

    public AwsProfileSession(AWSCredentials credentials, RegionEndpoint defaultRegion)
    {
        _credentials = credentials;
        _defaultRegion = defaultRegion;
    }

    public async Task<AwsIdentity> GetCallerIdentityAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new AmazonSecurityTokenServiceClient(_credentials, _defaultRegion);
            var response = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
            if (response.Account is not { Length: 12 } account ||
                !account.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(response.Arn))
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
            throw MapConnectionError(exception);
        }
        catch (AmazonClientException exception)
        {
            throw MapConnectionClientError(exception);
        }
    }

    public async Task<IReadOnlyList<Ec2InstanceResource>> ListEc2InstancesAsync(
        string region, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new AmazonEC2Client(_credentials, RegionEndpoint.GetBySystemName(region));
            var resources = new List<Ec2InstanceResource>();
            string? nextToken = null;
            do
            {
                var response = await client.DescribeInstancesAsync(
                    new DescribeInstancesRequest { NextToken = nextToken }, cancellationToken);
                foreach (var reservation in response.Reservations ?? [])
                {
                    foreach (var instance in reservation.Instances ?? [])
                    {
                        var nameTag = (instance.Tags ?? [])
                            .FirstOrDefault(tag => tag.Key == "Name");

                        resources.Add(new Ec2InstanceResource(
                            instance.InstanceId,
                            nameTag?.Value ?? "(이름 없음)",
                            instance.InstanceType?.Value ?? "—",
                            instance.State?.Name?.Value ?? "Unknown"));
                    }
                }
                nextToken = response.NextToken;
            } while (!string.IsNullOrEmpty(nextToken));

            return resources.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonServiceException exception)
        {
            throw MapQueryError(exception);
        }
        catch (AmazonClientException exception)
        {
            throw MapQueryClientError(exception);
        }
    }

    public async Task<IReadOnlyList<RdsInstanceResource>> ListRdsInstancesAsync(
        string region, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new AmazonRDSClient(_credentials, RegionEndpoint.GetBySystemName(region));
            var resources = new List<RdsInstanceResource>();
            string? marker = null;
            do
            {
                var response = await client.DescribeDBInstancesAsync(
                    new DescribeDBInstancesRequest { Marker = marker }, cancellationToken);
                foreach (var instance in response.DBInstances ?? [])
                {
                    resources.Add(new RdsInstanceResource(
                        instance.DBInstanceIdentifier,
                        instance.Engine,
                        instance.DBInstanceStatus ?? "Unknown"));
                }
                marker = response.Marker;
            } while (!string.IsNullOrEmpty(marker));

            return resources.OrderBy(item => item.DbInstanceIdentifier, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonServiceException exception)
        {
            throw MapQueryError(exception);
        }
        catch (AmazonClientException exception)
        {
            throw MapQueryClientError(exception);
        }
    }

    public async Task<IReadOnlyList<S3BucketResource>> ListS3BucketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new AmazonS3Client(_credentials, RegionEndpoint.USEast1);
            var buckets = new List<S3Bucket>();
            string? continuationToken = null;
            do
            {
                var response = await client.ListBucketsAsync(new ListBucketsRequest
                {
                    ContinuationToken = continuationToken,
                    MaxBuckets = 1000
                }, cancellationToken);
                buckets.AddRange(response.Buckets ?? []);
                continuationToken = response.ContinuationToken;
            } while (!string.IsNullOrEmpty(continuationToken));

            var resources = new List<S3BucketResource>(buckets.Count);
            foreach (var bucket in buckets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                resources.Add(new S3BucketResource(bucket.BucketName, bucket.BucketRegion ?? "확인 불가"));
            }

            return resources.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonServiceException exception)
        {
            throw MapQueryError(exception);
        }
        catch (AmazonClientException exception)
        {
            throw MapQueryClientError(exception);
        }
    }

    private static AwsConnectionException MapConnectionError(AmazonServiceException exception)
    {
        var code = exception.ErrorCode ?? string.Empty;
        var kind = code switch
        {
            "ExpiredToken" or "ExpiredTokenException" or "RequestExpired" => AwsConnectionFailureKind.ExpiredSession,
            "InvalidClientTokenId" or "UnrecognizedClientException" or "SignatureDoesNotMatch"
                or "InvalidSignatureException" or "AuthFailure" => AwsConnectionFailureKind.Authentication,
            "AccessDenied" or "AccessDeniedException" => AwsConnectionFailureKind.AccessDenied,
            _ => AwsConnectionFailureKind.Other
        };
        var message = kind switch
        {
            AwsConnectionFailureKind.ExpiredSession => "AWS 인증 세션이 만료되었습니다. 로컬 자격 증명을 갱신한 뒤 다시 연결하세요.",
            AwsConnectionFailureKind.Authentication => "AWS 자격 증명을 인증하지 못했습니다. 선택한 프로필을 확인하세요.",
            AwsConnectionFailureKind.AccessDenied => "AWS STS 요청이 거부되었습니다. 프로필의 권한과 정책을 확인하세요.",
            _ => "AWS STS 인증 확인에 실패했습니다. 잠시 후 다시 시도하세요."
        };
        return new AwsConnectionException(kind, message);
    }

    private static AwsConnectionException MapConnectionClientError(AmazonClientException exception)
    {
        var networkFailure = exception.InnerException is HttpRequestException or SocketException or IOException;
        return new AwsConnectionException(
            networkFailure ? AwsConnectionFailureKind.Network : AwsConnectionFailureKind.Other,
            networkFailure
                ? "AWS STS에 연결하지 못했습니다. 네트워크 상태를 확인하세요."
                : "AWS STS 요청을 처리하지 못했습니다. 네트워크와 로컬 프로필 설정을 확인하세요.");
    }

    private static AwsResourceQueryException MapQueryError(AmazonServiceException exception)
    {
        var code = exception.ErrorCode ?? string.Empty;
        var kind = code switch
        {
            "ExpiredToken" or "ExpiredTokenException" or "RequestExpired" => AwsQueryFailureKind.ExpiredSession,
            "InvalidClientTokenId" or "UnrecognizedClientException" or "SignatureDoesNotMatch"
                or "InvalidSignatureException" or "AuthFailure" => AwsQueryFailureKind.Authentication,
            "AccessDenied" or "AccessDeniedException" or "UnauthorizedOperation" => AwsQueryFailureKind.AccessDenied,
            _ => AwsQueryFailureKind.Other
        };
        var message = kind switch
        {
            AwsQueryFailureKind.ExpiredSession => "AWS 인증 세션이 만료되었습니다. 다시 연결하세요.",
            AwsQueryFailureKind.Authentication => "AWS 인증에 실패했습니다. 프로필을 다시 연결하세요.",
            AwsQueryFailureKind.AccessDenied => "이 서비스의 목록 조회 권한이 없습니다. AWS 권한 정책을 확인하세요.",
            _ => "AWS 리소스 목록을 불러오지 못했습니다. 잠시 후 다시 시도하세요."
        };
        return new AwsResourceQueryException(kind, message);
    }

    private static AwsResourceQueryException MapQueryClientError(AmazonClientException exception)
    {
        var networkFailure = exception.InnerException is HttpRequestException or SocketException or IOException;
        return new AwsResourceQueryException(
            networkFailure ? AwsQueryFailureKind.Network : AwsQueryFailureKind.Other,
            networkFailure
                ? "AWS 서비스에 연결하지 못했습니다. 네트워크 상태를 확인하세요."
                : "AWS 리소스 조회 요청을 처리하지 못했습니다. 프로필과 리전을 확인하세요.");
    }
}
