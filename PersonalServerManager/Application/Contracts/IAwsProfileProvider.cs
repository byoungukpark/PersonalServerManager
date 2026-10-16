namespace PersonalServerManager.Application.Contracts;

/// <summary>AWS CLI 공유 설정 파일의 프로필 목록을 제공한다.</summary>
public interface IAwsProfileProvider
{
    Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken);
}
