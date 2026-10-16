using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Application.Services;
using PersonalServerManager.Presentation.Commands;
using PersonalServerManager.Presentation.Navigation;

namespace PersonalServerManager.Presentation.ViewModels;

/// <summary>메인 셸의 현재 화면과 앱 전체 AWS 연결 상태를 제공한다.</summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly NavigationService _navigation;
    private readonly AwsConnectionService _connection;

    public ShellViewModel(NavigationService navigation, AwsConnectionService connection)
    {
        _navigation = navigation;
        _connection = connection;
        _navigation.PropertyChanged += OnNavigationChanged;
        _connection.PropertyChanged += OnConnectionChanged;
        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is AppPage page)
            {
                _navigation.NavigateTo(page);
            }
        });
    }

    public ICommand NavigateCommand { get; }
    public ViewModelBase? CurrentPageViewModel => _navigation.CurrentViewModel;
    public string CurrentPageTitle => _navigation.CurrentPage switch
    {
        AppPage.Dashboard => "대시보드",
        AppPage.AwsConnection => "AWS 연결 관리",
        AppPage.GroupWorkspace => "그룹 만들기",
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

    public bool IsDashboardSelected => _navigation.CurrentPage == AppPage.Dashboard;
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
            OnPropertyChanged(nameof(IsDashboardSelected));
            OnPropertyChanged(nameof(IsAwsConnectionSelected));
            OnPropertyChanged(nameof(IsGroupWorkspaceSelected));
            OnPropertyChanged(nameof(IsHistorySelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
        }
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
