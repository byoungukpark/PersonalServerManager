using PersonalServerManager.Application.Models;

namespace PersonalServerManager.Application.Contracts;

/// <summary>로컬 AWS CLI 공유 파일의 프로필을 조회한다.</summary>
public interface IAwsProfileProvider
{
    Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken);
}

/// <summary>선택된 프로필로 STS 호출을 수행하는 SDK 경계다.</summary>
public interface IStsIdentityGateway
{
    Task<AwsIdentity> GetCallerIdentityAsync(string profileName, CancellationToken cancellationToken);
}
