using PersonalServerManager.Models;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Services.Persistence;

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

/// <summary>등록 자원의 개별·그룹 제어와 앱 전체 단일 실행을 담당한다. 화면 이동과 무관하게 실행한다.</summary>
public sealed partial class ResourceControlService
{
    private sealed record StepResult(string Status, string Message)
    {
        public bool CanContinue => Status is "완료" or "건너뜀";
    }

    private readonly AwsConnectionService _connection;
    private readonly ServiceGroupService _groups;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<ControlTimeoutSettings> _settings;
    private readonly TimeProvider _clock;
    private readonly IControlHistoryStore? _history;

    public ResourceControlService(AwsConnectionService connection, ServiceGroupService groups,
        ControlActivityState activity, Func<ControlTimeoutSettings> settings, TimeProvider? clock = null,
        IControlHistoryStore? history = null)
    {
        _connection = connection;
        _groups = groups;
        Activity = activity;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _history = history;
        if (_history is not null)
            _history.AvailabilityChanged += () => ControlAvailabilityChanged?.Invoke();
    }

    public event Action? ControlAvailabilityChanged;

    public ControlActivityState Activity { get; }
    public long? ConnectionVersion => _connection.ActiveConnection?.ConnectionVersion;
    public bool CanControl(ServiceGroup? group) => group is not null && _connection.IsConnected &&
        _connection.ActiveConnection?.AccountId == group.AccountId && !Activity.IsControlRunning &&
        _history?.IsAvailable != false;

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

    /// <summary>저장된 시작 순서를 고정한다. 중지는 같은 목록을 정확히 뒤집는다.</summary>
    public static IReadOnlyList<GroupResourceRef> BuildGroupPlan(ServiceGroup group, ResourceControlAction action)
    {
        if (action is not (ResourceControlAction.Start or ResourceControlAction.Stop))
            throw new ServiceGroupException("그룹은 시작과 중지만 지원합니다.");
        var order = group.StartOrder.ToArray();
        return action == ResourceControlAction.Start ? order : order.Reverse().ToArray();
    }

    public string GroupConfirmationText(ServiceGroup group, ResourceControlAction action,
        IReadOnlyList<GroupResourceRef> plan)
    {
        var lines = plan.Select((item, index) =>
        {
            var resource = group.Resources.Single(resource => resource.Kind == item.Kind && resource.Id == item.Id);
            return $"{index + 1}. {item.Kind} {resource.Name} ({item.Id})";
        });
        return $"프로필: {_connection.ActiveConnection?.ProfileName}\n계정: {group.AccountId}\n" +
            $"그룹: {group.Name}\n리전: {group.Region}\n동작: 그룹 {ActionLabel(action)}\n\n실행 순서:\n" +
            string.Join("\n", lines) + "\n\n각 자원의 목표 상태를 확인한 뒤 다음 자원을 처리합니다. " +
            (action == ResourceControlAction.Start
                ? "AWS 비용이 발생할 수 있습니다."
                : "서비스 연결이 중단될 수 있으며 저장 장치 등 일부 비용은 계속 발생할 수 있습니다.");
    }

    /// <summary>확인창을 열었을 때의 연결과 저장된 등록 범위를 실행 직전에 다시 확인한다.</summary>
    public async Task ExecuteAsync(Guid groupId, AwsResourceKind kind, string resourceId,
        ResourceControlAction action, long confirmedVersion)
    {
        if (!await _gate.WaitAsync(0)) throw new ServiceGroupException("다른 제어 작업이 진행 중입니다.");
        var target = $"{kind} {resourceId} · {ActionLabel(action)}";
        ControlJournal? journal = null;
        var finalStatus = "실패";
        try
        {
            Activity.IsControlRunning = true;
            Activity.BeginIndividual();
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
            journal = await BeginJournalAsync(group, action, [resource]);
            var result = await RunStepAsync(session, group, resource, action, confirmedVersion, settings, target, 0, journal);
            finalStatus = result.Status;
        }
        catch (Exception exception)
        {
            Activity.Report(target, $"실패 · {FriendlyError(exception)}");
        }
        finally
        {
            if (journal is not null) await journal.FinishAsync(finalStatus, Activity.Message);
            Activity.RememberResult();
            Activity.IsControlRunning = false;
            _gate.Release();
        }
    }

    /// <summary>확인된 계획과 저장된 그룹 구성을 재검증한 뒤 자원을 한 개씩 완료까지 처리한다.</summary>
    public async Task ExecuteGroupAsync(Guid groupId, ResourceControlAction action, long confirmedVersion,
        IReadOnlyList<GroupResourceRef> confirmedPlan)
    {
        if (!await _gate.WaitAsync(0)) throw new ServiceGroupException("다른 제어 작업이 진행 중입니다.");
        var target = $"그룹 · {ActionLabel(action)}";
        var completedSteps = 0;
        ControlJournal? journal = null;
        var finalStatus = "실패";
        try
        {
            Activity.IsControlRunning = true;
            Activity.BeginIndividual();
            Activity.Report(target, "대기 · 등록 정보 확인 중");
            var group = (await _groups.ListAsync(default)).FirstOrDefault(item => item.Id == groupId)
                ?? throw new ServiceGroupException("등록된 그룹을 찾을 수 없습니다.");
            target = $"{group.Name} · 그룹 {ActionLabel(action)}";
            EnsureConnection(group, confirmedVersion);
            var plan = BuildGroupPlan(group, action);
            if (plan.Count == 0) throw new ServiceGroupException("제어할 EC2/RDS 자원이 없습니다.");
            if (!plan.SequenceEqual(confirmedPlan))
                throw new ServiceGroupException("확인 후 그룹의 실행 순서가 변경되었습니다. 다시 확인하세요.");
            var resources = plan.Select(item => group.Resources.SingleOrDefault(resource =>
                resource.Kind == item.Kind && resource.Id == item.Id)
                ?? throw new ServiceGroupException("확인한 자원이 그룹에서 변경되었습니다.")).ToArray();
            if (resources.Any(resource => resource.Kind is not (AwsResourceKind.Ec2 or AwsResourceKind.Rds)))
                throw new ServiceGroupException("그룹 제어는 EC2/RDS만 지원합니다.");
            var session = _connection.ActiveSession as IAwsResourceControlSession
                ?? throw new ServiceGroupException("현재 연결은 자원 제어를 지원하지 않습니다.");
            var settings = _settings();
            settings.Validate();
            journal = await BeginJournalAsync(group, action, resources, isGroup: true);
            Activity.BeginGroup(resources.Select((resource, index) =>
                new ControlStepState(index + 1, $"{resource.Kind} {resource.Name} ({resource.Id})", "대기", "명령 전"))
                .ToArray());

            for (var index = 0; index < resources.Length; index++)
            {
                // 확인된 취소는 현재 AWS 단계의 결과를 기다린 뒤 다음 명령 제출 전에 반영한다.
                if (Activity.CancelRemainingRequested)
                {
                    await MarkRemainingAsync(index, "사용자 취소", "명령을 보내지 않았습니다.", journal);
                    Activity.Report(target, $"사용자 취소 · {completedSteps}/{resources.Length}단계 완료. 남은 명령은 보내지 않았습니다.");
                    finalStatus = "사용자 취소";
                    return;
                }

                if (_history?.IsAvailable == false)
                {
                    await MarkRemainingAsync(index, "미실행", "작업 기록 오류로 명령을 보내지 않았습니다.", journal);
                    Activity.Report(target, "실패 · 작업 기록 오류로 후속 명령을 보내지 않았습니다.");
                    return;
                }

                var resource = resources[index];
                var stepTarget = $"{target} · {index + 1}/{resources.Length} {resource.Kind} {resource.Id}";
                Activity.UpdateStep(index, "진행 중", "상태 확인 중");
                StepResult result;
                try
                {
                    result = await RunStepAsync(session, group, resource, action, confirmedVersion, settings,
                        stepTarget, index, journal);
                }
                catch (Exception exception)
                {
                    result = new StepResult("실패", $"실패 · {FriendlyError(exception)}");
                    Activity.Report(stepTarget, result.Message);
                }
                Activity.UpdateStep(index, result.Status, result.Message);
                if (!result.CanContinue)
                {
                    await MarkRemainingAsync(index + 1, "미실행", "이전 단계가 완료되지 않아 명령을 보내지 않았습니다.", journal);
                    Activity.Report(target, $"{result.Status} · {index + 1}단계에서 중단. {completedSteps}/{resources.Length}단계 완료. 자동 복구하지 않습니다.");
                    finalStatus = result.Status;
                    return;
                }
                completedSteps++;
            }

            Activity.Report(target, $"완료 · {completedSteps}/{resources.Length}단계 목표 상태 확인 또는 건너뜀");
            finalStatus = "완료";
        }
        catch (Exception exception)
        {
            Activity.Report(target, $"실패 · {FriendlyError(exception)}");
        }
        finally
        {
            if (journal is not null) await journal.FinishAsync(finalStatus, Activity.Message);
            Activity.EndGroup();
            Activity.RememberResult();
            Activity.IsControlRunning = false;
            _gate.Release();
        }
    }

    private async Task MarkRemainingAsync(int fromIndex, string status, string message, ControlJournal? journal)
    {
        for (var index = fromIndex; index < Activity.Steps.Count; index++)
        {
            Activity.UpdateStep(index, status, message);
            if (journal is not null) await journal.StepAsync(index, status, message, complete: true);
        }
    }

    /// <summary>개별·그룹 제어가 같은 AWS 상태 검증, 명령 전송, 목표 상태 확인을 사용한다.</summary>
    private async Task<StepResult> RunStepAsync(IAwsResourceControlSession session, ServiceGroup group,
        GroupResource resource, ResourceControlAction action, long confirmedVersion,
        ControlTimeoutSettings settings, string target, int? stepIndex = null, ControlJournal? journal = null)
    {
        var kind = resource.Kind;
        var timeout = TimeSpan.FromMinutes(kind == AwsResourceKind.Ec2 ? settings.Ec2Minutes : settings.RdsMinutes);
        using var deadline = new CancellationTokenSource(timeout, _clock);
        var token = deadline.Token;
        var goal = action == ResourceControlAction.Stop ? "stopped" : kind == AwsResourceKind.Ec2 ? "running" : "available";
        var sent = false;
        try
        {
            if (journal is not null && stepIndex is { } position)
                await journal.StepAsync(position, "진행 중", "상태 확인 중", beforeCommand: true);
            // 제어 API 호출 전에 실제 AWS 상태와 제어 가능 여부를 다시 읽는다.
            var current = await session.ReadControlStateAsync(group.Region, kind, resource.Id, token);
            token.ThrowIfCancellationRequested();
            if (current.BlockReason is not null) throw new ServiceGroupException(current.BlockReason);
            if (action != ResourceControlAction.Reboot && current.State == goal)
            {
                if (journal is not null && stepIndex is { } skipped)
                    await journal.StepAsync(skipped, "건너뜀", $"이미 {goal} 상태", confirmed: true, complete: true);
                return ReportResult(target, "건너뜀", $"완료 · 이미 {goal} 상태여서 명령을 보내지 않았습니다.");
            }
            if (action != ResourceControlAction.Reboot && current.StartStopBlockReason is not null)
                throw new ServiceGroupException(current.StartStopBlockReason);

            var required = action == ResourceControlAction.Start ? "stopped" : kind == AwsResourceKind.Ec2 ? "running" : "available";
            if (current.State != required)
                throw new ServiceGroupException($"현재 상태({current.State})에서는 {ActionLabel(action)}할 수 없습니다.");

            EnsureConnection(group, confirmedVersion);
            token.ThrowIfCancellationRequested();
            if (journal is not null && stepIndex is { } sending)
                await journal.StepAsync(sending, "명령 전송", "AWS 명령 전송 직전", beforeCommand: true);
            ReportProgress(target, "명령 전송", "명령 전송 중 · 응답 대기", stepIndex);
            sent = true;
            // AWS 명령은 한 번만 제출한다. 응답 오류 후 자동 재전송하면 중복 제어가 될 수 있다.
            await session.SendControlAsync(group.Region, kind, resource.Id, action, token);
            token.ThrowIfCancellationRequested();
            ReportProgress(target, "AWS 요청 접수", "AWS 요청 접수", stepIndex);
            if (journal is not null && stepIndex is { } accepted)
                await journal.StepAsync(accepted, "AWS 요청 접수", "AWS 요청 접수", accepted: true);
            if (action == ResourceControlAction.Reboot)
            {
                if (journal is not null && stepIndex is { } reboot)
                    await journal.StepAsync(reboot, "요청 접수", "재부팅 요청 접수 · 완료 미확인", complete: true);
                return ReportResult(target, "요청 접수", "요청 접수 · 재부팅 완료 미확인");
            }

            var interval = TimeSpan.FromSeconds(kind == AwsResourceKind.Ec2 ? 5 : 15);
            while (true)
            {
                ReportProgress(target, "목표 상태 대기", $"목표 상태 대기 · {goal}", stepIndex);
                await Task.Delay(interval, _clock, token);
                EnsureConnection(group, confirmedVersion);
                // AWS 상태 조회 API로 실제 목표 상태를 확인하기 전에는 다음 그룹 단계로 넘어가지 않는다.
                current = await session.ReadControlStateAsync(group.Region, kind, resource.Id, token);
                token.ThrowIfCancellationRequested();
                if (current.State == goal)
                {
                    if (journal is not null && stepIndex is { } completed)
                        await journal.StepAsync(completed, "완료", $"{goal} 확인", confirmed: true, complete: true);
                    return ReportResult(target, "완료", $"완료 · {goal} 확인");
                }
                if (current.BlockReason is not null || current.State is "terminated" or "shutting-down" or "failed" or "incompatible-restore")
                    throw new ServiceGroupException(current.BlockReason ?? $"목표 상태에 도달할 수 없습니다 ({current.State}).");
                ReportProgress(target, "목표 상태 대기", $"목표 상태 대기 · 현재 {current.State}", stepIndex);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Activity.Report(target, "시간 초과 · 목표 상태를 확인하지 못했습니다. 이미 전송한 요청은 취소되지 않습니다.");
            await ReadFinalStateAsync(session, group, kind, resource.Id, confirmedVersion);
            if (journal is not null && stepIndex is { } timedOut)
                await journal.StepAsync(timedOut, "시간 초과", Activity.Message, complete: true);
            return new StepResult("시간 초과", Activity.Message);
        }
        catch (Exception exception)
        {
            if (exception is AwsResourceQueryException query &&
                query.Kind is AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession)
                _connection.MarkReconnectRequired();
            Activity.Report(target, $"실패 · {FriendlyError(exception)}" +
                (sent ? " 이미 전송한 요청이 진행 중일 수 있으므로 실제 상태를 다시 확인하세요." : ""));
            if (sent) await ReadFinalStateAsync(session, group, kind, resource.Id, confirmedVersion);
            if (journal is not null && stepIndex is { } failed)
                await journal.StepAsync(failed, "실패", Activity.Message, complete: true);
            return new StepResult("실패", Activity.Message);
        }
    }

    private StepResult ReportResult(string target, string status, string message)
    {
        Activity.Report(target, message);
        return new StepResult(status, message);
    }

    private void ReportProgress(string target, string status, string message, int? stepIndex)
    {
        Activity.Report(target, message);
        if (stepIndex is { } index && index < Activity.Steps.Count)
            Activity.UpdateStep(index, status, message);
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
