using System.Windows;
using System.Windows.Threading;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Services.Dialogs;
using PersonalServerManager.Navigation;
using PersonalServerManager.ViewModels;

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
            var settingsStore = new JsonAppSettingsStore();
            // 설정 파일 손상은 시작 시 알리고 종료한다. 이 단계에서는 AWS API를 호출하지 않는다.
            var restoredProfileName = await settingsStore.LoadAsync(CancellationToken.None);
            var controlActivity = new ControlActivityState();
            var profileProvider = new AwsProfileProvider();
            var sessionFactory = new AwsProfileSessionFactory(profileProvider);
            var connection = new AwsConnectionService(
                profileProvider,
                sessionFactory,
                controlActivity,
                new DispatcherSynchronizationContext(Dispatcher));
            var groupService = new ServiceGroupService(settingsStore,
                controlActivity: controlActivity, connection: connection);
            _ = await groupService.ListAsync(CancellationToken.None);

            var navigation = new NavigationService();
            var confirmation = new ConfirmationDialogService();
            var resourceQuery = new AwsResourceQueryService(connection);
            var groupStatus = new GroupStatusService(connection, resourceQuery);
            var metricQuery = new AwsCloudWatchMetricQuery(connection);
            var controlSettings = await settingsStore.LoadControlSettingsAsync(CancellationToken.None);
            var settings = new SettingsViewModel(settingsStore, confirmation, controlSettings);
            var control = new ResourceControlService(connection, groupService, controlActivity, () => settings.Current);
            var dashboard = new DashboardViewModel(navigation, groupService, groupStatus);
            var groupDetail = new GroupDetailViewModel(navigation, groupService, groupStatus, confirmation, control, metricQuery);
            dashboard.GroupStatusRead += groupDetail.ApplySharedStatus;
            dashboard.GroupStatusRefreshChanged += groupDetail.SetSharedStatusRefreshing;
            var groupManagement = new GroupManagementViewModel(groupService, connection, resourceQuery,
                confirmation);
            controlActivity.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(ControlActivityState.IsControlRunning) && !controlActivity.IsControlRunning)
                    await dashboard.RefreshStatusesAsync();
            };
            navigation.Register(AppPage.Dashboard, dashboard);
            navigation.Register(AppPage.GroupDetail, groupDetail);
            navigation.Register(AppPage.GroupManagement, groupManagement);
            groupDetail.ManageGroupRequested += id =>
            {
                navigation.NavigateTo(AppPage.GroupManagement);
                _ = groupManagement.LoadAsync(id);
            };
            groupManagement.ReturnToDetailRequested += id =>
            {
                navigation.NavigateTo(AppPage.GroupDetail);
                _ = groupDetail.LoadAsync(id);
            };
            groupManagement.Deleted += () => navigation.NavigateTo(AppPage.Dashboard);
            groupService.GroupUpdated += async _ => await dashboard.LoadAsync();
            var awsConnectionViewModel = new AwsConnectionViewModel(
                connection, settingsStore, confirmation, restoredProfileName);
            navigation.Register(AppPage.AwsConnection, awsConnectionViewModel);
            var wizard = new GroupWizardViewModel(
                connection, resourceQuery, groupService, navigation, confirmation,
                group =>
                {
                    navigation.NavigateTo(AppPage.GroupDetail);
                    _ = groupDetail.LoadAsync(group.Id);
                });
            wizard.GroupCreated += async (_, _) => await dashboard.LoadAsync();
            navigation.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NavigationService.CurrentPage) &&
                    navigation.CurrentPage == AppPage.GroupWorkspace && wizard.IsComplete)
                {
                    wizard.Reset();
                }
            };
            dashboard.OpenGroupRequested += id =>
            {
                navigation.NavigateTo(AppPage.GroupDetail);
                _ = groupDetail.LoadAsync(id);
            };
            navigation.Register(AppPage.GroupWorkspace, wizard);
            navigation.Register(AppPage.History, new PlaceholderViewModel(
                "작업 기록", "제어 작업 기록 조회 기능은 이후 단계에서 제공됩니다."));
            navigation.Register(AppPage.Settings, settings);
            await dashboard.LoadAsync();
            navigation.NavigateTo(AppPage.Dashboard);

            // 앱이 열려 있는 동안 등록 그룹의 기본 상태를 30초마다 갱신한다.
            var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            statusTimer.Tick += async (_, _) => await dashboard.RefreshStatusesAsync();
            var metricTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            metricTimer.Tick += async (_, _) => await groupDetail.RefreshMetricsIfDueAsync();
            connection.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName != nameof(AwsConnectionService.ActiveConnection) &&
                    args.PropertyName != nameof(AwsConnectionService.RequiresReconnect)) return;
                if (navigation.CurrentPage == AppPage.Dashboard)
                    await dashboard.RefreshStatusesAsync();
                else if (navigation.CurrentPage == AppPage.GroupDetail && groupDetail.CurrentGroupId is { } id)
                {
                    groupDetail.CancelMetricRefresh();
                    await groupDetail.LoadAsync(id);
                    groupDetail.ResetMetricsForConnection();
                }
            };
            navigation.PropertyChanged += async (_, args) =>
            {
                if (args.PropertyName == nameof(NavigationService.CurrentPage) &&
                    navigation.CurrentPage == AppPage.Dashboard)
                    await dashboard.RefreshStatusesAsync();
            };

            var window = new MainWindow
            {
                DataContext = new ShellViewModel(navigation, connection, wizard, controlActivity,
                    confirmation, groupManagement)
            };
            MainWindow = window;
            window.Closed += (_, _) =>
            {
                statusTimer.Stop();
                metricTimer.Stop();
                groupDetail.CancelMetricRefresh();
            };
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
                    if (controlActivity.IsControlRunning && !await confirmation.ConfirmAsync(
                        "AWS 제어 진행 중", "앱을 종료하시겠습니까? 이미 전송한 AWS 명령은 서버에서 계속 진행될 수 있으며 완료 여부를 더 이상 확인하지 않습니다.",
                        "종료")) return;
                    if (!await wizard.ConfirmLeaveAsync())
                    {
                        return;
                    }
                    if (navigation.CurrentPage == AppPage.GroupManagement &&
                        !await groupManagement.ConfirmLeaveAsync()) return;
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
            statusTimer.Start();
            metricTimer.Start();
        }
        catch (AppSettingsException exception)
        {
            MessageBox.Show(exception.Message, "앱 설정 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
