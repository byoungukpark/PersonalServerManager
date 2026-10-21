using PersonalServerManager.Models;
using PersonalServerManager.Services.Aws;

namespace PersonalServerManager.Services.Groups;

/// <summary>시작·중지 제한시간. 값은 앱 설정과 모의 검증에서 전달한다.</summary>
public sealed record ControlTimeoutSettings(int Ec2Minutes = 10, int RdsMinutes = 30)
{
    public void Validate()
    {
        if (Ec2Minutes is < 1 or > 60 || RdsMinutes is < 1 or > 120)
            throw new ServiceGroupException("제한시간은 EC2 1~60분, RDS 1~120분으로 입력하세요.");
    }
}

/// <summary>등록 자원의 개별 제어와 앱 전체 단일 실행을 담당한다. 화면 이동과 무관하게 실행한다.</summary>
public sealed class ResourceControlService
{
    private readonly AwsConnectionService _connection;
    private readonly ServiceGroupService _groups;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<ControlTimeoutSettings> _settings;
    private readonly TimeProvider _clock;

    public ResourceControlService(AwsConnectionService connection, ServiceGroupService groups,
        ControlActivityState activity, Func<ControlTimeoutSettings> settings, TimeProvider? clock = null)
    {
        _connection = connection;
        _groups = groups;
        Activity = activity;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
    }

    public ControlActivityState Activity { get; }
    public long? ConnectionVersion => _connection.ActiveConnection?.ConnectionVersion;
    public bool CanControl(ServiceGroup? group) => group is not null && _connection.IsConnected &&
        _connection.ActiveConnection?.AccountId == group.AccountId && !Activity.IsControlRunning;

    public static string ActionLabel(ResourceControlAction action) => action switch
    {
        ResourceControlAction.Start => "시작",
        ResourceControlAction.Stop => "중지",
        ResourceControlAction.Reboot => "재부팅",
        _ => "지원하지 않는 명령"
    };

    public string ConfirmationText(ServiceGroup group, GroupResource resource, ResourceControlAction action) =>
        $"프로필: {_connection.ActiveConnection?.ProfileName}\n계정: {group.AccountId}\n" +
        $"그룹: {group.Name}\n리전: {group.Region}\n대상: {resource.Name} ({resource.Id})\n동작: {ActionLabel(action)}\n\n" +
        (action == ResourceControlAction.Start
            ? "실행에 따른 AWS 비용이 발생할 수 있습니다."
            : "서비스 연결이 중단될 수 있습니다. 저장 장치 등 일부 비용은 계속 발생할 수 있습니다.") +
        (action == ResourceControlAction.Reboot ? "\n재부팅은 요청 접수만 확인하며 완료 여부는 확인하지 않습니다." : "");

    /// <summary>확인창을 열었을 때의 연결과 저장된 등록 범위를 실행 직전에 다시 확인한다.</summary>
    public async Task ExecuteAsync(Guid groupId, AwsResourceKind kind, string resourceId,
        ResourceControlAction action, long confirmedVersion)
    {
        if (!await _gate.WaitAsync(0)) throw new ServiceGroupException("다른 제어 작업이 진행 중입니다.");
        var target = $"{kind} {resourceId} · {ActionLabel(action)}";
        try
        {
            Activity.IsControlRunning = true;
            Activity.Report(target, "대기 · 등록 정보 확인 중");
            if (!Enum.IsDefined(action) || kind == AwsResourceKind.S3 ||
                (kind == AwsResourceKind.Rds && action == ResourceControlAction.Reboot))
                throw new ServiceGroupException("지원하지 않는 제어 명령입니다.");

            var group = (await _groups.ListAsync(default)).FirstOrDefault(item => item.Id == groupId)
                ?? throw new ServiceGroupException("등록된 그룹을 찾을 수 없습니다.");
            var resource = group.Resources.FirstOrDefault(item => item.Kind == kind && item.Id == resourceId)
                ?? throw new ServiceGroupException("그룹에 등록되지 않은 자원은 제어할 수 없습니다.");
            target = $"{group.Name} / {kind} {resourceId} · {ActionLabel(action)}";
            EnsureConnection(group, confirmedVersion);
            var session = _connection.ActiveSession as IAwsResourceControlSession
                ?? throw new ServiceGroupException("현재 연결은 자원 제어를 지원하지 않습니다.");
            var settings = _settings();
            settings.Validate();
            var timeout = TimeSpan.FromMinutes(kind == AwsResourceKind.Ec2 ? settings.Ec2Minutes : settings.RdsMinutes);
            using var deadline = new CancellationTokenSource(timeout, _clock);
            var token = deadline.Token;
            var goal = action == ResourceControlAction.Stop ? "stopped" : kind == AwsResourceKind.Ec2 ? "running" : "available";
            var sent = false;
            try
            {
                var current = await session.ReadControlStateAsync(group.Region, kind, resource.Id, token);
                token.ThrowIfCancellationRequested();
                if (current.BlockReason is not null) throw new ServiceGroupException(current.BlockReason);
                if (action != ResourceControlAction.Reboot && current.StartStopBlockReason is not null)
                    throw new ServiceGroupException(current.StartStopBlockReason);
                if (action != ResourceControlAction.Reboot && current.State == goal)
                {
                    Activity.Report(target, $"완료 · 이미 {goal} 상태여서 명령을 보내지 않았습니다.");
                    return;
                }
                var required = action == ResourceControlAction.Start ? "stopped" : kind == AwsResourceKind.Ec2 ? "running" : "available";
                if (current.State != required)
                    throw new ServiceGroupException($"현재 상태({current.State})에서는 {ActionLabel(action)}할 수 없습니다.");

                EnsureConnection(group, confirmedVersion);
                token.ThrowIfCancellationRequested();
                Activity.Report(target, "명령 전송 중 · 응답 대기");
                sent = true;
                await session.SendControlAsync(group.Region, kind, resource.Id, action, token);
                token.ThrowIfCancellationRequested();
                Activity.Report(target, "AWS 요청 접수");
                if (action == ResourceControlAction.Reboot)
                {
                    Activity.Report(target, "요청 접수 · 재부팅 완료 미확인");
                    return;
                }

                var interval = TimeSpan.FromSeconds(kind == AwsResourceKind.Ec2 ? 5 : 15);
                while (true)
                {
                    Activity.Report(target, $"목표 상태 대기 · {goal}");
                    await Task.Delay(interval, _clock, token);
                    EnsureConnection(group, confirmedVersion);
                    current = await session.ReadControlStateAsync(group.Region, kind, resource.Id, token);
                    token.ThrowIfCancellationRequested();
                    if (current.State == goal)
                    {
                        Activity.Report(target, $"완료 · {goal} 확인");
                        return;
                    }
                    if (current.BlockReason is not null || current.State is "terminated" or "shutting-down" or "failed" or "incompatible-restore")
                        throw new ServiceGroupException(current.BlockReason ?? $"목표 상태에 도달할 수 없습니다 ({current.State}).");
                    Activity.Report(target, $"목표 상태 대기 · 현재 {current.State}");
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                Activity.Report(target, "시간 초과 · 목표 상태를 확인하지 못했습니다. 이미 전송한 요청은 취소되지 않습니다.");
                await ReadFinalStateAsync(session, group, kind, resource.Id, confirmedVersion);
            }
            catch (Exception exception)
            {
                if (exception is AwsResourceQueryException query &&
                    query.Kind is AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession)
                    _connection.MarkReconnectRequired();
                Activity.Report(target, $"실패 · {FriendlyError(exception)}" +
                    (sent ? " 이미 전송한 요청이 진행 중일 수 있으므로 실제 상태를 다시 확인하세요." : ""));
                if (sent) await ReadFinalStateAsync(session, group, kind, resource.Id, confirmedVersion);
            }
        }
        catch (Exception exception)
        {
            Activity.Report(target, $"실패 · {FriendlyError(exception)}");
        }
        finally
        {
            Activity.RememberResult();
            Activity.IsControlRunning = false;
            _gate.Release();
        }
    }

    private void EnsureConnection(ServiceGroup group, long version)
    {
        if (!_connection.IsConnected || _connection.ActiveConnection?.AccountId != group.AccountId ||
            _connection.ActiveConnection.ConnectionVersion != version)
            throw new ServiceGroupException("AWS 연결이 변경되었거나 계정이 일치하지 않습니다. 다시 확인하세요.");
    }

    /// <summary>실패·시간 초과 후 읽기만 한 번 더 시도하며 제어 명령은 재전송하지 않는다.</summary>
    private async Task ReadFinalStateAsync(IAwsResourceControlSession session, ServiceGroup group,
        AwsResourceKind kind, string id, long version)
    {
        try
        {
            EnsureConnection(group, version);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await session.ReadControlStateAsync(group.Region, kind, id, deadline.Token);
            Activity.Report(Activity.Target, $"{Activity.Message} 마지막 재조회: {result.State}.");
        }
        catch (Exception)
        {
            Activity.Report(Activity.Target, $"{Activity.Message} 마지막 상태 재조회도 확인하지 못했습니다.");
        }
    }

    private static string FriendlyError(Exception exception) =>
        exception is ServiceGroupException or AwsResourceQueryException or Persistence.AppSettingsException
            ? exception.Message : "작업 결과를 확인하지 못했습니다. AWS 상태와 연결을 확인하세요.";
}
