using System.Windows;
using PersonalServerManager.Presentation.Navigation;
using PersonalServerManager.Presentation.ViewModels;

namespace PersonalServerManager;

public partial class App : System.Windows.Application
{
    /// <summary>앱 시작 시 화면 객체를 조립하고 첫 화면을 지정한다.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var navigation = new NavigationService();
        navigation.Register(AppPage.Dashboard, new DashboardViewModel(navigation));
        navigation.Register(AppPage.AwsConnection, new PlaceholderViewModel(
            "AWS 연결 관리", "AWS 프로필 연결과 계정 확인 기능은 다음 단계에서 제공됩니다."));
        navigation.Register(AppPage.GroupWorkspace, new PlaceholderViewModel(
            "그룹 생성·관리", "서비스 그룹 생성과 관리 기능은 다음 단계에서 제공됩니다."));
        navigation.Register(AppPage.History, new PlaceholderViewModel(
            "작업 기록", "제어 작업 기록 조회 기능은 이후 단계에서 제공됩니다."));
        navigation.Register(AppPage.Settings, new PlaceholderViewModel(
            "설정", "앱 설정 기능은 이후 단계에서 제공됩니다."));
        navigation.NavigateTo(AppPage.Dashboard);

        var window = new MainWindow
        {
            DataContext = new ShellViewModel(navigation)
        };
        MainWindow = window;
        window.Show();
    }
}
