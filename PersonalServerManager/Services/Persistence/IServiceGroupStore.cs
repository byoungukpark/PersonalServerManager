using PersonalServerManager.Models;
using PersonalServerManager.Services.Groups;

namespace PersonalServerManager.Services.Persistence;

/// <summary>앱의 로컬 서비스 그룹 구성을 읽고 확정된 변경만 저장한다.</summary>
public interface IServiceGroupStore
{
    Task<IReadOnlyList<ServiceGroup>> LoadGroupsAsync(CancellationToken cancellationToken);
    Task AddGroupAsync(ServiceGroup group, CancellationToken cancellationToken);
    Task ReplaceGroupAsync(ServiceGroup group, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken);
    Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken);
}

public interface IControlSettingsStore
{
    Task<ControlTimeoutSettings> LoadControlSettingsAsync(CancellationToken cancellationToken);
    Task SaveControlSettingsAsync(ControlTimeoutSettings settings, CancellationToken cancellationToken);
}
