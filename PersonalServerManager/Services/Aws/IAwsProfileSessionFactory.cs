using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

/// <summary>프로필 인증 결과와 같은 자격 증명 세션으로 AWS 읽기 API를 호출한다.</summary>
public interface IAwsProfileSessionFactory
{
    Task<IAwsProfileSession> CreateAsync(string profileName, CancellationToken cancellationToken);
}

/// <summary>STS 검증에 사용한 프로필 자격 증명을 유지하는 읽기 전용 세션이다.</summary>
public interface IAwsProfileSession
{
    Task<AwsIdentity> GetCallerIdentityAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Ec2InstanceResource>> ListEc2InstancesAsync(string region, CancellationToken cancellationToken);
    Task<IReadOnlyList<RdsInstanceResource>> ListRdsInstancesAsync(string region, CancellationToken cancellationToken);
    Task<IReadOnlyList<S3BucketResource>> ListS3BucketsAsync(CancellationToken cancellationToken);
}
