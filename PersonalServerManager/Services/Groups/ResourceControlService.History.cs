using PersonalServerManager.Models;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Services.Persistence;

namespace PersonalServerManager.Services.Groups;

public sealed partial class ResourceControlService
{
    private sealed class ControlJournal
    {
        private readonly IControlHistoryStore _store;
        private readonly ControlActivityState _activity;
        private readonly TimeProvider _clock;
        private readonly ControlStepRecord[] _steps;
        public ControlJobRecord Job { get; }

        public ControlJournal(IControlHistoryStore store, ControlActivityState activity, TimeProvider clock,
            ControlJobRecord job, ControlStepRecord[] steps)
        { _store = store; _activity = activity; _clock = clock; Job = job; _steps = steps; }

        /// <summary>첫 AWS 명령 전에 작업과 전체 예정 단계를 한 트랜잭션으로 남긴다.</summary>
        public async Task StartAsync()
        {
            try { await _store.StartAsync(Job, _steps, CancellationToken.None); }
            catch (AppSettingsException exception)
            {
                _activity.ReportHistoryFailure(exception.Message);
                throw new ServiceGroupException("작업 기록을 저장하지 못해 AWS 제어를 시작하지 않았습니다.");
            }
        }

        public async Task StepAsync(int index, string status, string message, bool accepted = false,
            bool confirmed = false, bool complete = false, bool beforeCommand = false)
        {
            var previous = _steps[index];
            var next = previous with
            {
                StartedAt = status is "미실행" or "사용자 취소" ? previous.StartedAt :
                    previous.StartedAt ?? _clock.GetUtcNow(),
                CompletedAt = complete ? _clock.GetUtcNow() : null,
                RequestAccepted = previous.RequestAccepted || accepted,
                GoalConfirmed = previous.GoalConfirmed || confirmed,
                Status = status,
                Message = message
            };
            _steps[index] = next;
            try { await _store.UpdateStepAsync(next, CancellationToken.None); }
            catch (AppSettingsException exception)
            {
                _activity.ReportHistoryFailure(exception.Message);
                if (beforeCommand)
                    throw new ServiceGroupException("작업 기록 오류로 AWS 명령을 보내지 않았습니다.");
            }
        }

        public async Task FinishAsync(string status, string message)
        {
            if (!_store.IsAvailable) return;
            try { await _store.FinishAsync(Job with
                { CompletedAt = _clock.GetUtcNow(), Status = status, Message = message }, CancellationToken.None); }
            catch (AppSettingsException exception) { _activity.ReportHistoryFailure(exception.Message); }
        }
    }

    private async Task<ControlJournal?> BeginJournalAsync(ServiceGroup group, ResourceControlAction action,
        IReadOnlyList<GroupResource> resources, bool isGroup = false)
    {
        if (_history is null) return null;
        if (!_history.IsAvailable)
            throw new ServiceGroupException(_history.FailureMessage);
        var jobId = Guid.NewGuid();
        var started = _clock.GetUtcNow();
        var job = new ControlJobRecord(jobId, group.Id, group.Name, group.AccountId, group.Region,
            isGroup ? $"그룹 {ActionLabel(action)}" : ActionLabel(action),
            started, null, "진행 중", "제어 시작 전");
        var steps = resources.Select((resource, index) => new ControlStepRecord(jobId, index + 1,
            resource.Kind.ToString(), resource.Id, ActionLabel(action), null, null,
            false, false, "대기", "명령 전")).ToArray();
        var journal = new ControlJournal(_history, Activity, _clock, job, steps);
        await journal.StartAsync();
        return journal;
    }
}
