using System.Windows;
using System.Windows.Threading;
using PersonalServerManager.Application.Services;
using PersonalServerManager.Infrastructure.Aws;
using PersonalServerManager.Infrastructure.Persistence;
using PersonalServerManager.Presentation.Dialogs;
using PersonalServerManager.Presentation.Navigation;
using PersonalServerManager.Presentation.ViewModels;

namespace PersonalServerManager;

public partial class App : System.Windows.Application
{
    /// <summary>앱 시작 시 화면 객체를 조립하고 첫 화면을 지정한다.</summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var selectedProfileStore = new JsonSelectedProfileStore();
            // 설정 파일 손상은 시작 시 알리고 종료한다. 이 단계에서는 AWS API를 호출하지 않는다.
            var restoredProfileName = await selectedProfileStore.LoadAsync(CancellationToken.None);

            var controlActivity = new ControlActivityState();
            var profileProvider = new AwsProfileProvider();
            var stsGateway = new StsIdentityGateway(profileProvider);
            var connection = new AwsConnectionService(
                profileProvider,
                stsGateway,
                controlActivity,
                new DispatcherSynchronizationContext(Dispatcher));

            var navigation = new NavigationService();
            navigation.Register(AppPage.Dashboard, new DashboardViewModel(navigation));
            var awsConnectionViewModel = new AwsConnectionViewModel(
                connection, selectedProfileStore, new MessageBoxConfirmationService(), restoredProfileName);
            navigation.Register(AppPage.AwsConnection, awsConnectionViewModel);
            navigation.Register(AppPage.GroupWorkspace, new PlaceholderViewModel(
                "그룹 생성·관리", "서비스 그룹 생성과 관리 기능은 다음 단계에서 제공됩니다."));
            navigation.Register(AppPage.History, new PlaceholderViewModel(
                "작업 기록", "제어 작업 기록 조회 기능은 이후 단계에서 제공됩니다."));
            navigation.Register(AppPage.Settings, new PlaceholderViewModel(
                "설정", "앱 설정 기능은 이후 단계에서 제공됩니다."));
            navigation.NavigateTo(AppPage.Dashboard);

            var window = new MainWindow
            {
                DataContext = new ShellViewModel(navigation, connection)
            };
            MainWindow = window;
            var closeAfterProfileSave = false;
            var checkingProfileSave = false;
            window.Closing += async (_, args) =>
            {
                if (closeAfterProfileSave)
                {
                    return;
                }

                args.Cancel = true;
                if (checkingProfileSave)
                {
                    return;
                }

                checkingProfileSave = true;
                try
                {
                    await awsConnectionViewModel.FlushSelectedProfileAsync();
                    closeAfterProfileSave = true;
                    _ = window.Dispatcher.BeginInvoke(new Action(window.Close));
                }
                catch (Exception exception)
                {
                    var message = exception is AppSettingsException
                        ? exception.Message
                        : "마지막으로 선택한 AWS 프로필을 저장하지 못했습니다.";
                    var choice = MessageBox.Show(
                        window,
                        $"{message}\n\n저장하지 않고 종료하시겠습니까?",
                        "앱 설정 저장 오류",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (choice == MessageBoxResult.Yes)
                    {
                        closeAfterProfileSave = true;
                        _ = window.Dispatcher.BeginInvoke(new Action(window.Close));
                    }
                }
                finally
                {
                    checkingProfileSave = false;
                }
            };
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
        catch (AppSettingsException exception)
        {
            MessageBox.Show(exception.Message, "앱 설정 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
