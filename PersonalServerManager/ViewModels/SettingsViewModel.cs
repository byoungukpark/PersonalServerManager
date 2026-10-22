using System.Windows.Input;
using PersonalServerManager.Commands;
using PersonalServerManager.Services.Dialogs;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Persistence;

namespace PersonalServerManager.ViewModels;

public sealed record HistoryRetentionChoice(int Days, string Label);

/// <summary>제어·Health 제한시간의 초안과 확정 저장값을 분리한다.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private string _ec2Minutes;
    private string _rdsMinutes;
    private int _healthTimeoutSeconds;
    private int _historyRetentionDays;
    private string _message = "저장한 값은 다음 점검·제어 작업부터 적용됩니다.";
    private readonly IHistoryRetentionStore _historyStore;
    private readonly IConfirmationService _confirmation;

    public SettingsViewModel(IHistoryRetentionStore historyStore,
        IConfirmationService confirmation, ControlTimeoutSettings settings, int healthTimeoutSeconds,
        int historyRetentionDays = 30)
    {
        _historyStore = historyStore;
        _confirmation = confirmation;
        Current = settings;
        _ec2Minutes = settings.Ec2Minutes.ToString();
        _rdsMinutes = settings.RdsMinutes.ToString();
        _healthTimeoutSeconds = healthTimeoutSeconds;
        CurrentHealthTimeoutSeconds = healthTimeoutSeconds;
        _historyRetentionDays = historyRetentionDays;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => true,
            exception => Message = exception is AppSettingsException or ServiceGroupException
                ? exception.Message : "설정을 저장하지 못했습니다.");
    }

    public ControlTimeoutSettings Current { get; private set; }
    public int CurrentHealthTimeoutSeconds { get; private set; }
    public string Ec2Minutes { get => _ec2Minutes; set => SetProperty(ref _ec2Minutes, value); }
    public string RdsMinutes { get => _rdsMinutes; set => SetProperty(ref _rdsMinutes, value); }
    public int HealthTimeoutSeconds { get => _healthTimeoutSeconds; set => SetProperty(ref _healthTimeoutSeconds, value); }
    public IReadOnlyList<int> HealthTimeoutChoices { get; } = [3, 5, 10, 15];
    public int HistoryRetentionDays { get => _historyRetentionDays; set => SetProperty(ref _historyRetentionDays, value); }
    public IReadOnlyList<HistoryRetentionChoice> HistoryRetentionChoices { get; } =
        [new(7, "7일"), new(30, "30일 (기본)"), new(0, "무기한")];
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public ICommand SaveCommand { get; }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(Ec2Minutes, out var ec2) || !int.TryParse(RdsMinutes, out var rds))
            throw new ServiceGroupException("제한시간은 정수 분으로 입력하세요.");
        var settings = new ControlTimeoutSettings(ec2, rds);
        settings.Validate();
        if (!HealthTimeoutChoices.Contains(HealthTimeoutSeconds))
            throw new ServiceGroupException("Health 제한시간은 3, 5, 10, 15초 중에서 선택하세요.");
        if (HistoryRetentionDays is not (0 or 7 or 30))
            throw new ServiceGroupException("작업 기록 보존 기간을 선택하세요.");
        if (!await _confirmation.ConfirmAsync("설정 저장",
            $"EC2: {Current.Ec2Minutes}분 → {ec2}분\nRDS: {Current.RdsMinutes}분 → {rds}분\nHealth: {HealthTimeoutSeconds}초\n작업 기록 보존: {(HistoryRetentionDays == 0 ? "무기한" : $"{HistoryRetentionDays}일")}\n\n다음 작업부터 적용합니다.",
            "저장", cancellationToken)) return;
        await _historyStore.SaveRuntimeSettingsAsync(settings, HealthTimeoutSeconds, HistoryRetentionDays, cancellationToken);
        Current = settings;
        CurrentHealthTimeoutSeconds = HealthTimeoutSeconds;
        Message = "제어·Health 제한시간과 작업 기록 보존 기간을 저장했습니다. 보존 정리는 다음 실행 시 적용됩니다.";
    }
}
