using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

/// <summary>연결된 계정의 리소스 목록을 읽는 경계다.</summary>
public interface IAwsResourceQuery
{
    Task<IReadOnlyList<Ec2InstanceResource>> ListEc2Async(string region, CancellationToken cancellationToken);
    Task<IReadOnlyList<RdsInstanceResource>> ListRdsAsync(string region, CancellationToken cancellationToken);
    Task<IReadOnlyList<S3BucketResource>> ListS3Async(CancellationToken cancellationToken);
}
