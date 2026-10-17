using PersonalServerManager.Domain.Models;

namespace PersonalServerManager.Application.Contracts;

/// <summary>앱의 로컬 서비스 그룹 구성을 읽고 확정된 새 그룹만 저장한다.</summary>
public interface IServiceGroupStore
{
    Task<IReadOnlyList<ServiceGroup>> LoadGroupsAsync(CancellationToken cancellationToken);
    Task AddGroupAsync(ServiceGroup group, CancellationToken cancellationToken);
}
