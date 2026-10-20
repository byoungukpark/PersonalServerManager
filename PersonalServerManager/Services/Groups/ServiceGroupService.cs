using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Groups;

/// <summary>그룹 생성 초안을 검증하고 사용자가 확정한 그룹만 로컬 설정에 저장한다.</summary>
public sealed class ServiceGroupService
{
    private readonly IServiceGroupStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _creationGate = new(1, 1);

    public ServiceGroupService(IServiceGroupStore store, TimeProvider? timeProvider = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<ServiceGroup>> ListAsync(CancellationToken cancellationToken) =>
        _store.LoadGroupsAsync(cancellationToken);

    public async Task<ServiceGroup> CreateAsync(
        string name,
        string accountId,
        string region,
        string profileName,
        IReadOnlyList<GroupResource> resources,
        IReadOnlyList<GroupResourceRef> startOrder,
        CancellationToken cancellationToken)
    {
        await _creationGate.WaitAsync(cancellationToken);
        try
        {
            var normalizedName = GroupRules.NormalizeName(name);
            var resourceSnapshot = (resources ?? throw new ServiceGroupException("리소스 목록이 없습니다."))
                .Select(resource => resource is null ? null! : resource with
                {
                    Region = resource.Kind == AwsResourceKind.S3 ? resource.Region : region,
                    Name = string.IsNullOrWhiteSpace(resource.Name) ? resource.Id : resource.Name
                })
                .ToArray();
            var orderSnapshot = (startOrder ?? throw new ServiceGroupException("시작 순서가 없습니다."))
                .ToArray();
            var now = _timeProvider.GetUtcNow();
            var group = new ServiceGroup(
                Guid.NewGuid(), normalizedName, accountId, region, profileName, now, now,
                resourceSnapshot, orderSnapshot);
            GroupRules.Validate(group);

            var existing = await _store.LoadGroupsAsync(cancellationToken);
            GroupRules.EnsureUnique(group, existing);
            await _store.AddGroupAsync(group, cancellationToken);
            return group;
        }
        finally
        {
            _creationGate.Release();
        }
    }
}
