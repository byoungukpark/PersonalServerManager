using System.Windows.Input;
using PersonalServerManager.Commands;
using PersonalServerManager.Services.Dialogs;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Persistence;

namespace PersonalServerManager.ViewModels;

/// <summary>개별 제어 제한시간의 초안과 확정 저장값을 분리한다.</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private string _ec2Minutes;
    private string _rdsMinutes;
    private string _message = "저장한 값은 다음 제어 작업부터 적용됩니다.";
    private readonly IControlSettingsStore _store;
    private readonly IConfirmationService _confirmation;

    public SettingsViewModel(IControlSettingsStore store, IConfirmationService confirmation, ControlTimeoutSettings settings)
    {
        _store = store;
        _confirmation = confirmation;
        Current = settings;
        _ec2Minutes = settings.Ec2Minutes.ToString();
        _rdsMinutes = settings.RdsMinutes.ToString();
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => true,
            exception => Message = exception is AppSettingsException or ServiceGroupException
                ? exception.Message : "설정을 저장하지 못했습니다.");
    }

    public ControlTimeoutSettings Current { get; private set; }
    public string Ec2Minutes { get => _ec2Minutes; set => SetProperty(ref _ec2Minutes, value); }
    public string RdsMinutes { get => _rdsMinutes; set => SetProperty(ref _rdsMinutes, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public ICommand SaveCommand { get; }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (!int.TryParse(Ec2Minutes, out var ec2) || !int.TryParse(RdsMinutes, out var rds))
            throw new ServiceGroupException("제한시간은 정수 분으로 입력하세요.");
        var settings = new ControlTimeoutSettings(ec2, rds);
        settings.Validate();
        if (!await _confirmation.ConfirmAsync("제어 제한시간 저장",
            $"EC2: {Current.Ec2Minutes}분 → {ec2}분\nRDS: {Current.RdsMinutes}분 → {rds}분\n\n다음 제어부터 적용합니다.",
            "저장", cancellationToken)) return;
        await _store.SaveControlSettingsAsync(settings, cancellationToken);
        Current = settings;
        Message = "제어 제한시간을 저장했습니다. 다음 작업부터 적용됩니다.";
    }
}
