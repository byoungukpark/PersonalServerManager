using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Aws;

namespace PersonalServerManager.Services.Groups;

/// <summary>그룹 생성 초안을 검증하고 사용자가 확정한 그룹만 로컬 설정에 저장한다.</summary>
public sealed class ServiceGroupService
{
    private readonly IServiceGroupStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _creationGate = new(1, 1);
    private readonly ControlActivityState? _controlActivity;
    private readonly AwsConnectionService? _connection;

    public ServiceGroupService(IServiceGroupStore store, TimeProvider? timeProvider = null,
        ControlActivityState? controlActivity = null, AwsConnectionService? connection = null)
    {
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _controlActivity = controlActivity;
        _connection = connection;
    }

    public Task<IReadOnlyList<ServiceGroup>> ListAsync(CancellationToken cancellationToken) =>
        _store.LoadGroupsAsync(cancellationToken);

    /// <summary>AWS 자원은 유지하고 로컬 그룹 구성만 삭제한다.</summary>
    public async Task DeleteAsync(Guid groupId, CancellationToken cancellationToken)
    {
        if (_controlActivity?.IsControlRunning == true)
            throw new ServiceGroupException("제어 작업 중에는 그룹을 삭제할 수 없습니다.");
        await _store.DeleteGroupAsync(groupId, cancellationToken);
        GroupDeleted?.Invoke(groupId);
    }

    public event Action<Guid>? GroupDeleted;
    public event Action<Guid>? GroupUpdated;

    /// <summary>기존 ID·계정·리전을 유지하고 새 연결 자원의 계정을 확인한 뒤 로컬 구성만 교체한다.</summary>
    public async Task<ServiceGroup> UpdateAsync(ServiceGroup original, string name,
        IReadOnlyList<GroupResource> resources, IReadOnlyList<GroupResourceRef> startOrder,
        long? confirmedConnectionVersion, CancellationToken cancellationToken)
    {
        if (_controlActivity?.IsControlRunning == true)
            throw new ServiceGroupException("제어 작업 중에는 그룹을 편집할 수 없습니다.");
        var resourceSnapshot = resources.Select(resource => resource with
        {
            Region = resource.Kind == AwsResourceKind.S3 ? resource.Region : original.Region,
            Name = string.IsNullOrWhiteSpace(resource.Name) ? resource.Id : resource.Name
        }).ToArray();
        var added = resourceSnapshot.Any(resource => !original.Resources.Any(old =>
            old.Kind == resource.Kind && string.Equals(old.Id, resource.Id, StringComparison.OrdinalIgnoreCase)));
        if (added && (_connection?.IsConnected != true ||
            _connection.ActiveConnection?.AccountId != original.AccountId ||
            _connection.ActiveConnection.ConnectionVersion != confirmedConnectionVersion))
            throw new ServiceGroupException("새 자원을 연결하려면 해당 그룹의 AWS 계정으로 다시 연결하세요.");

        var now = _timeProvider.GetUtcNow();
        var updated = original with
        {
            Name = GroupRules.NormalizeName(name),
            UpdatedAt = now > original.UpdatedAt ? now : original.UpdatedAt.AddTicks(1),
            Resources = resourceSnapshot,
            StartOrder = startOrder.ToArray()
        };
        GroupRules.Validate(updated);
        await _store.ReplaceGroupAsync(updated, original.UpdatedAt, cancellationToken);
        GroupUpdated?.Invoke(updated.Id);
        return updated;
    }

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
