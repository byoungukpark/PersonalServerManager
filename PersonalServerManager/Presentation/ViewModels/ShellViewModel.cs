using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Presentation.Commands;
using PersonalServerManager.Presentation.Navigation;

namespace PersonalServerManager.Presentation.ViewModels;

/// <summary>메인 셸의 현재 화면, 메뉴 선택, 연결 표시용 기본 상태를 제공한다.</summary>
public sealed class ShellViewModel : ViewModelBase
{
    private readonly NavigationService _navigation;

    public ShellViewModel(NavigationService navigation)
    {
        _navigation = navigation;
        _navigation.PropertyChanged += OnNavigationChanged;
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
        AppPage.GroupWorkspace => "그룹 생성·관리",
        AppPage.History => "작업 기록",
        AppPage.Settings => "설정",
        _ => string.Empty
    };

    public string ConnectionStatusText => "AWS 미연결";
    public string ProfileNameText => "프로필 없음";
    public string AccountIdText => "계정 ID —";

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
}
