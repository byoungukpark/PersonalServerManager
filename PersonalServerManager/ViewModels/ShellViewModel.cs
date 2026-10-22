using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Commands;
using PersonalServerManager.Navigation;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Dialogs;

namespace PersonalServerManager.ViewModels;

/// <summary>메인 셸의 현재 화면과 앱 전체 AWS 연결 상태를 제공한다.</summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly NavigationService _navigation;
    private readonly AwsConnectionService _connection;
    private readonly GroupWizardViewModel _wizard;
    private readonly GroupManagementViewModel _management;
    private readonly IConfirmationService _confirmation;
    private readonly AsyncRelayCommand _cancelRemainingCommand;

    public ShellViewModel(NavigationService navigation, AwsConnectionService connection, GroupWizardViewModel wizard,
        ControlActivityState controlActivity, IConfirmationService confirmation,
        GroupManagementViewModel management)
    {
        _navigation = navigation;
        _connection = connection;
        _wizard = wizard;
        _management = management;
        _confirmation = confirmation;
        ControlActivity = controlActivity;
        _cancelRemainingCommand = new AsyncRelayCommand(CancelRemainingAsync,
            () => ControlActivity.CanCancelRemaining, ReportCancelRemainingError);
        ControlActivity.PropertyChanged += OnControlActivityChanged;
        _navigation.PropertyChanged += OnNavigationChanged;
        _connection.PropertyChanged += OnConnectionChanged;
        NavigateCommand = new RelayCommand(Navigate);
    }

    private async Task CancelRemainingAsync(CancellationToken token)
    {
        if (!await _confirmation.ConfirmAsync("남은 그룹 작업 취소",
            "현재 진행 중인 자원의 결과 확인은 계속합니다. 이후 아직 제출하지 않은 AWS 명령을 취소하시겠습니까?\n\n이미 전송한 명령은 되돌리지 않습니다.",
            "남은 작업 취소", token)) return;
        ControlActivity.RequestCancelRemaining();
    }

    private void ReportCancelRemainingError(Exception exception)
    {
        ControlActivity.Report(ControlActivity.Target, "취소 확인창을 열지 못했습니다. 다시 시도해 주세요.");
    }

    private void OnControlActivityChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ControlActivityState.CanCancelRemaining))
            _cancelRemainingCommand.RaiseCanExecuteChanged();
    }

    private async void Navigate(object? parameter)
    {
        if (parameter is not AppPage page) return;
        try
        {
            if (_navigation.CurrentPage == AppPage.GroupWorkspace && page != AppPage.GroupWorkspace)
            {
                if (!await _wizard.ConfirmLeaveAsync())
                {
                    NotifyNavigationSelection();
                    return;
                }
                _wizard.Reset();
            }
            if (_navigation.CurrentPage == AppPage.GroupManagement && page != AppPage.GroupManagement &&
                !await _management.ConfirmLeaveAsync())
            {
                NotifyNavigationSelection();
                return;
            }
            _navigation.NavigateTo(page);
        }
        catch (Exception)
        {
            _wizard.ReportNavigationError();
            _management.ReportNavigationError();
            NotifyNavigationSelection();
        }
    }

    public ICommand NavigateCommand { get; }
    public ICommand CancelRemainingCommand => _cancelRemainingCommand;
    public ControlActivityState ControlActivity { get; }
    public ViewModelBase? CurrentPageViewModel => _navigation.CurrentViewModel;
    public string CurrentPageTitle => _navigation.CurrentPage switch
    {
        AppPage.Dashboard => "대시보드",
        AppPage.AwsConnection => "AWS 연결 관리",
        AppPage.GroupWorkspace => "그룹 만들기",
        AppPage.GroupDetail => "그룹 상세",
        AppPage.GroupManagement => "그룹 관리",
        AppPage.History => "작업 기록",
        AppPage.Settings => "설정",
        _ => string.Empty
    };

    public string ConnectionStatusText => _connection.RequiresReconnect
        ? "AWS 재연결 필요"
        : _connection.IsConnected ? "AWS 연결됨" : "AWS 미연결";
    public string ProfileNameText => _connection.ActiveConnection?.ProfileName ?? "프로필 없음";
    public string AccountIdText => _connection.ActiveConnection is { } active
        ? $"계정 ID {active.AccountId}"
        : "계정 ID —";
    public bool IsConnected => _connection.IsConnected;
    public bool RequiresReconnect => _connection.RequiresReconnect;

    public bool IsDashboardSelected => _navigation.CurrentPage is AppPage.Dashboard or AppPage.GroupDetail or AppPage.GroupManagement;
    public bool IsAwsConnectionSelected => _navigation.CurrentPage == AppPage.AwsConnection;
    public bool IsGroupWorkspaceSelected => _navigation.CurrentPage == AppPage.GroupWorkspace;
    public bool IsHistorySelected => _navigation.CurrentPage == AppPage.History;
    public bool IsSettingsSelected => _navigation.CurrentPage == AppPage.Settings;

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationService.CurrentViewModel))
        {
            OnPropertyChanged(nameof(CurrentPageViewModel));
        }

        if (e.PropertyName == nameof(NavigationService.CurrentPage))
        {
            OnPropertyChanged(nameof(CurrentPageTitle));
            NotifyNavigationSelection();
        }
    }

    private void NotifyNavigationSelection()
    {
        OnPropertyChanged(nameof(IsDashboardSelected));
        OnPropertyChanged(nameof(IsAwsConnectionSelected));
        OnPropertyChanged(nameof(IsGroupWorkspaceSelected));
        OnPropertyChanged(nameof(IsHistorySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(ProfileNameText));
        OnPropertyChanged(nameof(AccountIdText));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(RequiresReconnect));
    }
}
