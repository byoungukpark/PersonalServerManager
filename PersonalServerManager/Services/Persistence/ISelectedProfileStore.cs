namespace PersonalServerManager.Services.Persistence;

/// <summary>인증 정보 없이 마지막으로 선택한 프로필 이름만 보존한다.</summary>
public interface ISelectedProfileStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(string? profileName, CancellationToken cancellationToken);
}
