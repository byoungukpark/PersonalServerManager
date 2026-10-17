using PersonalServerManager.Application.Contracts;

namespace PersonalServerManager.Infrastructure.Persistence;

/// <summary>기존 프로필 저장 호출과 호환되는 설정 저장소 어댑터다.</summary>
public sealed class JsonSelectedProfileStore : ISelectedProfileStore
{
    private readonly JsonAppSettingsStore _settings = new();

    public Task<string?> LoadAsync(CancellationToken cancellationToken) =>
        _settings.LoadAsync(cancellationToken);

    public Task SaveAsync(string? profileName, CancellationToken cancellationToken) =>
        _settings.SaveAsync(profileName, cancellationToken);
}
