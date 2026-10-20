using PersonalServerManager.Services.Aws;
using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Groups;

/// <summary>저장된 그룹의 계정을 확인한 뒤 등록 자원의 기본 상태만 읽는다.</summary>
public sealed class GroupStatusService
{
    private readonly AwsConnectionService _connection;
    private readonly IAwsResourceQuery _query;
    private readonly TimeProvider _clock;

    public GroupStatusService(AwsConnectionService connection, IAwsResourceQuery query, TimeProvider? clock = null)
    {
        _connection = connection;
        _query = query;
        _clock = clock ?? TimeProvider.System;
    }

    public string? GetAccessMessage(ServiceGroup group)
    {
        if (!_connection.IsConnected || _connection.ActiveConnection is not { } active)
        {
            return _connection.RequiresReconnect
                ? "AWS 재연결이 필요합니다. AWS 연결 관리에서 다시 연결하세요."
                : "AWS 미연결 · AWS 연결 관리에서 프로필을 연결하세요.";
        }

        return string.Equals(active.AccountId, group.AccountId, StringComparison.Ordinal)
            ? null
            : $"계정 불일치 · 그룹 {group.AccountId}, 현재 {active.AccountId}. AWS 조회가 차단되었습니다.";
    }

    public async Task<GroupReadResult> ReadAsync(ServiceGroup group, CancellationToken cancellationToken)
    {
        if (group.Resources.Count == 0)
        {
            return new GroupReadResult(null, []);
        }

        var access = GetAccessMessage(group);
        if (access is not null)
        {
            return new GroupReadResult(access, []);
        }

        var version = _connection.ActiveConnection!.ConnectionVersion;
        var results = new List<ServiceReadResult>();
        foreach (var kind in Enum.GetValues<AwsResourceKind>())
        {
            if (!group.Resources.Any(resource => resource.Kind == kind))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var changed = GetAccessMessage(group);
            if (changed is not null || _connection.ActiveConnection?.ConnectionVersion != version)
            {
                return new GroupReadResult(changed ?? "AWS 연결이 변경되어 이전 조회 결과를 폐기했습니다.", []);
            }

            try
            {
                var states = await ReadServiceAsync(group, kind, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var afterRead = GetAccessMessage(group);
                if (afterRead is not null || _connection.ActiveConnection?.ConnectionVersion != version)
                {
                    return new GroupReadResult(afterRead ?? "AWS 연결이 변경되어 이전 조회 결과를 폐기했습니다.", []);
                }

                results.Add(new ServiceReadResult(kind, states, _clock.GetUtcNow(), null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AwsResourceQueryException exception)
            {
                if (exception.Kind is AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession)
                {
                    _connection.MarkReconnectRequired();
                    return new GroupReadResult(GetAccessMessage(group), results);
                }

                results.Add(new ServiceReadResult(kind, new Dictionary<string, string>(), null, exception.Message));
            }
            catch (AwsConnectionException)
            {
                return new GroupReadResult(GetAccessMessage(group) ?? "AWS 연결이 변경되어 이전 조회 결과를 폐기했습니다.", []);
            }
            catch (Exception)
            {
                results.Add(new ServiceReadResult(kind, new Dictionary<string, string>(), null,
                    "리소스 상태를 조회하지 못했습니다. 잠시 후 다시 시도하세요."));
            }
        }

        return new GroupReadResult(null, results);
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadServiceAsync(
        ServiceGroup group, AwsResourceKind kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case AwsResourceKind.Ec2:
                return (await _query.ListEc2Async(group.Region, cancellationToken))
                    .ToDictionary(item => item.InstanceId, item => item.State, StringComparer.Ordinal);
            case AwsResourceKind.Rds:
                return (await _query.ListRdsAsync(group.Region, cancellationToken))
                    .ToDictionary(item => item.DbInstanceIdentifier, item => item.Status, StringComparer.Ordinal);
            case AwsResourceKind.S3:
                var buckets = (await _query.ListS3Async(cancellationToken))
                    .Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
                return group.Resources.Where(resource => resource.Kind == AwsResourceKind.S3)
                    .ToDictionary(resource => resource.Id,
                        resource => buckets.Contains(resource.Id) ? "조회 가능" : "버킷 없음",
                        StringComparer.Ordinal);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
