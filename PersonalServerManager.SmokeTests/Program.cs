using PersonalServerManager.Services.Aws;
using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Dialogs;
using PersonalServerManager.Navigation;
using PersonalServerManager.ViewModels;
using PersonalServerManager.Views;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("미연결·계정 불일치 차단", AccessGateAsync),
    ("정상·빈 목록과 카드 상태", SuccessAndEmptyAsync),
    ("서비스별 권한·통신 오류와 이전 상태", IndependentFailuresAsync),
    ("프로필 오류와 STS 재확인 실패", ConnectionFailuresAsync),
    ("인증 만료 후 재연결 요구", ExpiredSessionAsync),
    ("연결 변경 후 늦은 응답 폐기", LateResponseAsync),
    ("원 조회 경계의 이전 세션 결과 폐기", QueryBoundaryAsync),
    ("그룹 마법사 조회 실패·빈 목록·늦은 응답", WizardQueryPathsAsync),
    ("로컬 그룹 삭제·복원·자원 재등록", DeletePersistenceAsync),
    ("그룹 삭제 취소·저장 실패·미연결", DeleteViewModelAsync),
    ("조회 중 그룹 삭제와 늦은 응답", DeleteDuringQueryAsync),
    ("개별 제어 정상 전이·재부팅 접수·목표 상태 건너뛰기", ControlSuccessAsync),
    ("제어 게이트·미지원·권한·인증·통신 오류", ControlGuardsAsync),
    ("제어 중 프로필·삭제·중복 차단과 시간 초과", ControlTimeoutAsync),
    ("제어 확인 취소·연결 변경·화면 이동", ControlViewModelAsync),
    ("그룹 순차 시작·역순 중지·목표 상태 건너뛰기", GroupSequenceAsync),
    ("그룹 실패·시간 초과·남은 작업 취소", GroupInterruptionAsync),
    ("대시보드·그룹 상세 WPF 화면 생성", WpfViewsAsync)
};

foreach (var (name, run) in checks)
{
    await run();
    Console.WriteLine($"PASS {name}");
}

static async Task ControlSuccessAsync()
{
    foreach (var kind in new[] { AwsResourceKind.Ec2, AwsResourceKind.Rds })
    foreach (var action in new[] { ResourceControlAction.Start, ResourceControlAction.Stop })
    {
        var fixture = await ControlFixtureAsync();
        var id = kind == AwsResourceKind.Ec2 ? "i-1" : "db-1";
        var goal = action == ResourceControlAction.Stop ? "stopped" : kind == AwsResourceKind.Ec2 ? "running" : "available";
        var initial = action == ResourceControlAction.Start ? "stopped" : kind == AwsResourceKind.Ec2 ? "running" : "available";
        fixture.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot(
            fixture.Session.Sent == 0 ? initial : goal));
        var operation = fixture.Control.ExecuteAsync(fixture.Group.Id, kind, id, action, fixture.Version);
        await UntilAsync(() => fixture.Clock.PendingTimers >= 2);
        fixture.Clock.Advance(TimeSpan.FromSeconds(kind == AwsResourceKind.Ec2 ? 5 : 15));
        await operation;
        Check(fixture.Activity.Message.Contains("완료") && fixture.Session.Sent == 1 && !fixture.Activity.IsControlRunning,
            "명령 접수와 목표 상태 완료 오류");
        await fixture.Control.ExecuteAsync(fixture.Group.Id, kind, id, action, fixture.Version);
        Check(fixture.Session.Sent == 1 && fixture.Activity.Message.Contains("이미"), "이미 목표 상태인데 명령을 전송함");
    }
    var reboot = await ControlFixtureAsync();
    reboot.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot("running"));
    await reboot.Control.ExecuteAsync(reboot.Group.Id, AwsResourceKind.Ec2, "i-1", ResourceControlAction.Reboot, reboot.Version);
    Check(reboot.Activity.Message.Contains("재부팅 완료 미확인") && reboot.Session.Sent == 1, "재부팅을 완료로 오인함");
}

static async Task ControlGuardsAsync()
{
    var fixture = await ControlFixtureAsync();
    foreach (var target in new[] { (AwsResourceKind.S3, "bucket-1", ResourceControlAction.Start),
        (AwsResourceKind.Ec2, "i-unregistered", ResourceControlAction.Start),
        (AwsResourceKind.Rds, "db-1", ResourceControlAction.Reboot) })
    {
        await fixture.Control.ExecuteAsync(fixture.Group.Id, target.Item1, target.Item2, target.Item3, fixture.Version);
        Check(fixture.Activity.Message.Contains("실패") && fixture.Session.Sent == 0, "미지원/미등록 제어 실행");
    }
    fixture.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot("available", "미지원 RDS 구성"));
    await fixture.Control.ExecuteAsync(fixture.Group.Id, AwsResourceKind.Rds, "db-1", ResourceControlAction.Stop, fixture.Version);
    Check(fixture.Session.Sent == 0 && fixture.Activity.Message.Contains("미지원"), "미지원 RDS 실행");
    fixture.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot("pending"));
    await fixture.Control.ExecuteAsync(fixture.Group.Id, AwsResourceKind.Ec2, "i-1", ResourceControlAction.Start, fixture.Version);
    Check(fixture.Session.Sent == 0, "전환 중 자원 제어");
    fixture.Store.Groups[0] = fixture.Group with { AccountId = "222222222222" };
    await fixture.Control.ExecuteAsync(fixture.Group.Id, AwsResourceKind.Ec2, "i-1", ResourceControlAction.Start, fixture.Version);
    Check(fixture.Session.Sent == 0 && fixture.Activity.Message.Contains("계정"), "계정 불일치 제어");

    foreach (var failure in new[] { AwsQueryFailureKind.AccessDenied, AwsQueryFailureKind.Network, AwsQueryFailureKind.ExpiredSession })
    {
        var failed = await ControlFixtureAsync();
        failed.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot("stopped"));
        failed.Session.Send = (_, _, _, _, _) => throw new AwsResourceQueryException(failure, "테스트 오류");
        await failed.Control.ExecuteAsync(failed.Group.Id, AwsResourceKind.Ec2, "i-1", ResourceControlAction.Start, failed.Version);
        Check(failed.Activity.Message.Contains("실패") && failed.Activity.Message.Contains("진행 중일 수"), "실패/접수 불확실성 안내 누락");
        Check(failed.Connection.RequiresReconnect == (failure == AwsQueryFailureKind.ExpiredSession), "오류별 인증 상태 오류");
    }
}

static async Task ControlTimeoutAsync()
{
    var fixture = await ControlFixtureAsync();
    fixture.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot("stopped"));
    var operation = fixture.Control.ExecuteAsync(fixture.Group.Id, AwsResourceKind.Ec2, "i-1", ResourceControlAction.Start, fixture.Version);
    await UntilAsync(() => fixture.Clock.PendingTimers >= 2);
    Check(!fixture.Connection.CanChangeConnection, "제어 중 연결 변경 허용");
    try { fixture.Connection.Disconnect(); throw new Exception("제어 중 해제 허용"); }
    catch (AwsConnectionException) { }
    try { await fixture.Connection.ConnectAsync("other", default); throw new Exception("제어 중 전환 허용"); }
    catch (AwsConnectionException) { }
    try { await fixture.Connection.VerifyAsync(default); throw new Exception("제어 중 연결 재확인 허용"); }
    catch (AwsConnectionException) { }
    try { await fixture.Groups.DeleteAsync(fixture.Group.Id, default); throw new Exception("제어 중 삭제 허용"); }
    catch (ServiceGroupException) { }
    try
    {
        await fixture.Control.ExecuteAsync(fixture.Group.Id, AwsResourceKind.Ec2, "i-1", ResourceControlAction.Start, fixture.Version);
        throw new Exception("중복 제어 허용");
    }
    catch (ServiceGroupException) { }
    fixture.Clock.Advance(TimeSpan.FromMinutes(11));
    await operation;
    Check(fixture.Activity.Message.Contains("시간 초과") && fixture.Activity.Message.Contains("취소되지") &&
        fixture.Connection.CanChangeConnection && fixture.Session.Sent == 1, "시간 초과 처리 오류");
}

static async Task ControlViewModelAsync()
{
    var fixture = await ControlFixtureAsync();
    var navigation = new NavigationService();
    var query = new FakeQuery { Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>([new("i-1", "웹", "t3.micro", "stopped")]) };
    var status = new GroupStatusService(fixture.Connection, query);
    var confirmation = new FakeConfirmation { Result = false };
    var detail = new GroupDetailViewModel(navigation, fixture.Groups, status, confirmation, fixture.Control);
    navigation.Register(AppPage.GroupDetail, detail);
    navigation.Register(AppPage.Dashboard, new DashboardViewModel(navigation, fixture.Groups, status));
    navigation.NavigateTo(AppPage.GroupDetail);
    await detail.LoadAsync(fixture.Group.Id);
    detail.SelectedResource = detail.Resources.Single(row => row.Kind == "EC2");
    await detail.ControlSelectedAsync(ResourceControlAction.Start);
    Check(fixture.Session.Sent == 0, "확인 취소 후 명령 전송");
    confirmation.Result = true;
    fixture.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot(fixture.Session.Sent == 0 ? "stopped" : "running"));
    var operation = detail.ControlSelectedAsync(ResourceControlAction.Start);
    await UntilAsync(() => fixture.Clock.PendingTimers >= 2);
    Check(!detail.DeleteCommand.CanExecute(null), "제어 중 삭제 버튼 활성");
    navigation.NavigateTo(AppPage.Dashboard);
    fixture.Clock.Advance(TimeSpan.FromSeconds(5));
    await operation;
    Check(navigation.CurrentPage == AppPage.Dashboard && fixture.Activity.Message.Contains("완료"), "화면 이동 시 제어 중단/강제 이동");
    await fixture.Connection.ConnectAsync("other", default);
    await fixture.Control.ExecuteAsync(fixture.Group.Id, AwsResourceKind.Ec2, "i-1", ResourceControlAction.Start, fixture.Version);
    Check(fixture.Activity.Message.Contains("실패") && fixture.Session.Sent == 1, "이전 확인창 연결로 제어함");
}

static async Task GroupSequenceAsync()
{
    var unconfirmed = await ControlFixtureAsync();
    var navigation = new NavigationService();
    var status = new GroupStatusService(unconfirmed.Connection, new FakeQuery());
    var confirmation = new FakeConfirmation { Result = false };
    var detail = new GroupDetailViewModel(navigation, unconfirmed.Groups, status, confirmation, unconfirmed.Control);
    await detail.LoadAsync(unconfirmed.Group.Id);
    await detail.ControlGroupAsync(ResourceControlAction.Start);
    Check(unconfirmed.Session.Sent == 0, "그룹 제어 확인 취소 후 AWS 명령 전송");

    foreach (var action in new[] { ResourceControlAction.Start, ResourceControlAction.Stop })
    {
        var fixture = await ControlFixtureAsync();
        var plan = ResourceControlService.BuildGroupPlan(fixture.Group, action);
        var initial = action == ResourceControlAction.Start ? "stopped" : "running";
        fixture.Session.ControlState = (_, kind, id, _) => Task.FromResult(new ResourceControlSnapshot(
            fixture.Session.SentCommands.Any(command => command.Id == id)
                ? action == ResourceControlAction.Stop ? "stopped" : kind == AwsResourceKind.Ec2 ? "running" : "available"
                : action == ResourceControlAction.Stop && kind == AwsResourceKind.Rds ? "available" : initial));
        var operation = fixture.Control.ExecuteGroupAsync(fixture.Group.Id, action, fixture.Version, plan);
        await UntilAsync(() => fixture.Session.Sent == 1 && fixture.Clock.PendingTimers >= 2);
        Check(fixture.Session.SentCommands[0].Id == plan[0].Id && fixture.Activity.Steps[1].Status == "대기",
            "첫 목표 상태 확인 전에 다음 자원을 제어함");
        fixture.Clock.Advance(TimeSpan.FromSeconds(plan[0].Kind == AwsResourceKind.Ec2 ? 5 : 15));
        await UntilAsync(() => fixture.Session.Sent == 2 && fixture.Clock.PendingTimers >= 2);
        Check(fixture.Session.SentCommands[1].Id == plan[1].Id, "저장된 시작/역순 중지 계획 불일치");
        fixture.Clock.Advance(TimeSpan.FromSeconds(plan[1].Kind == AwsResourceKind.Ec2 ? 5 : 15));
        await operation;
        Check(fixture.Activity.Steps.All(step => step.Status == "완료") &&
            fixture.Activity.Message.StartsWith("완료") && !fixture.Activity.IsControlRunning,
            "그룹 제어 목표 상태 완료 오류");

        await fixture.Control.ExecuteGroupAsync(fixture.Group.Id, action, fixture.Version, plan);
        Check(fixture.Session.Sent == 2 && fixture.Activity.Steps.All(step => step.Status == "건너뜀"),
            "이미 목표 상태인 자원에 AWS 명령을 보냄");
    }
}

static async Task GroupInterruptionAsync()
{
    var failed = await ControlFixtureAsync();
    var extra = new GroupResource(AwsResourceKind.Ec2, "i-2", "작업 서버");
    var group = failed.Group with
    {
        Resources = [.. failed.Group.Resources, extra],
        StartOrder = [.. failed.Group.StartOrder, new GroupResourceRef(AwsResourceKind.Ec2, extra.Id)]
    };
    failed.Store.Groups[0] = group;
    failed.Session.ControlState = (_, kind, id, _) => Task.FromResult(new ResourceControlSnapshot(
        id == "db-1" ? "failed" : failed.Session.SentCommands.Any(command => command.Id == id) ? "running" : "stopped"));
    var operation = failed.Control.ExecuteGroupAsync(group.Id, ResourceControlAction.Start, failed.Version, group.StartOrder);
    await UntilAsync(() => failed.Session.Sent == 1 && failed.Clock.PendingTimers >= 2);
    failed.Clock.Advance(TimeSpan.FromSeconds(5));
    await operation;
    Check(failed.Session.Sent == 1 && failed.Activity.Steps[1].Status == "실패" &&
        failed.Activity.Steps[2].Status == "미실행", "중간 단계 실패 후 후속 명령 제출");

    var cancelled = await ControlFixtureAsync();
    cancelled.Session.ControlState = (_, kind, id, _) => Task.FromResult(new ResourceControlSnapshot(
        cancelled.Session.SentCommands.Any(command => command.Id == id)
            ? kind == AwsResourceKind.Ec2 ? "running" : "available" : "stopped"));
    var cancelOperation = cancelled.Control.ExecuteGroupAsync(cancelled.Group.Id, ResourceControlAction.Start,
        cancelled.Version, cancelled.Group.StartOrder);
    await UntilAsync(() => cancelled.Session.Sent == 1 && cancelled.Clock.PendingTimers >= 2);
    try
    {
        await cancelled.Control.ExecuteAsync(cancelled.Group.Id, AwsResourceKind.Rds, "db-1",
            ResourceControlAction.Start, cancelled.Version);
        throw new Exception("그룹 제어 중 개별 제어를 허용함");
    }
    catch (ServiceGroupException) { }
    cancelled.Activity.RequestCancelRemaining();
    Check(cancelled.Session.Sent == 1 && cancelled.Activity.IsControlRunning, "취소 요청이 현재 단계를 중단함");
    cancelled.Clock.Advance(TimeSpan.FromSeconds(5));
    await cancelOperation;
    Check(cancelled.Session.Sent == 1 && cancelled.Activity.Steps[0].Status == "완료" &&
        cancelled.Activity.Steps[1].Status == "사용자 취소" && cancelled.Activity.Message.StartsWith("사용자 취소"),
        "현재 단계 완료 후 남은 작업 취소 실패");

    var timedOut = await ControlFixtureAsync();
    timedOut.Session.ControlState = (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot("stopped"));
    var timeoutOperation = timedOut.Control.ExecuteGroupAsync(timedOut.Group.Id, ResourceControlAction.Start,
        timedOut.Version, timedOut.Group.StartOrder);
    await UntilAsync(() => timedOut.Session.Sent == 1 && timedOut.Clock.PendingTimers >= 2);
    timedOut.Clock.Advance(TimeSpan.FromMinutes(11));
    await timeoutOperation;
    Check(timedOut.Session.Sent == 1 && timedOut.Activity.Steps[0].Status == "시간 초과" &&
        timedOut.Activity.Steps[1].Status == "미실행", "시간 초과 후 후속 명령 제출");

    var guarded = await ControlFixtureAsync();
    await guarded.Control.ExecuteGroupAsync(guarded.Group.Id, ResourceControlAction.Start, guarded.Version,
        [new GroupResourceRef(AwsResourceKind.Rds, "db-1")]);
    Check(guarded.Session.Sent == 0 && guarded.Activity.Message.Contains("실행 순서"),
        "확인 후 변경된 그룹 계획을 허용함");
    await guarded.Control.ExecuteGroupAsync(guarded.Group.Id, ResourceControlAction.Start, guarded.Version + 1,
        guarded.Group.StartOrder);
    Check(guarded.Session.Sent == 0 && guarded.Activity.Message.Contains("연결"),
        "확인 후 변경된 AWS 연결을 허용함");

    var empty = guarded.Group with { Id = Guid.NewGuid(), Resources = [], StartOrder = [] };
    guarded.Store.Groups.Add(empty);
    await guarded.Control.ExecuteGroupAsync(empty.Id, ResourceControlAction.Start, guarded.Version, []);
    Check(guarded.Session.Sent == 0 && guarded.Activity.Message.Contains("제어할"),
        "빈 그룹에 AWS 명령을 보냄");
}

static async Task<(AwsConnectionService Connection, FakeSession Session, ServiceGroup Group,
    FakeGroupStore Store, ServiceGroupService Groups, ResourceControlService Control,
    ControlActivityState Activity, ManualControlClock Clock, long Version)> ControlFixtureAsync()
{
    var activity = new ControlActivityState();
    var factory = new FakeSessionFactory();
    var connection = new AwsConnectionService(new FakeProfiles(), factory, activity);
    await connection.ConnectAsync("first", default);
    var group = Group();
    var store = new FakeGroupStore { Groups = [group] };
    var groups = new ServiceGroupService(store, controlActivity: activity);
    var clock = new ManualControlClock();
    var control = new ResourceControlService(connection, groups, activity, () => new ControlTimeoutSettings(), clock);
    return (connection, factory.LastSession!, group, store, groups, control, activity, clock, connection.ActiveConnection!.ConnectionVersion);
}

static async Task DeletePersistenceAsync()
{
    var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"psm-delete-{Guid.NewGuid():N}");
    var path = System.IO.Path.Combine(directory, "settings.json");
    try
    {
        var store = new JsonAppSettingsStore(path);
        var service = new ServiceGroupService(store);
        await store.SaveAsync("saved-profile", default);
        await store.SaveControlSettingsAsync(new ControlTimeoutSettings(12, 40), default);
        Check((await new JsonAppSettingsStore(path).LoadControlSettingsAsync(default)).RdsMinutes == 40, "제어 설정 복원 실패");
        var sample = Group();
        var group = await service.CreateAsync(sample.Name, sample.AccountId, sample.Region, sample.ProfileName,
            sample.Resources, sample.StartOrder, default);
        var empty = Group() with { Id = Guid.NewGuid(), Name = "빈 그룹", Resources = [], StartOrder = [] };
        await store.AddGroupAsync(empty, default);
        await service.DeleteAsync(group.Id, default);
        var restored = new JsonAppSettingsStore(path);
        Check((await restored.LoadGroupsAsync(default)).Single().Id == empty.Id, "대상 외 그룹 변경 또는 삭제 복원 실패");
        Check(await restored.LoadAsync(default) == "saved-profile", "프로필 설정 유실");
        await service.CreateAsync(group.Name, group.AccountId, group.Region, group.ProfileName,
            group.Resources, group.StartOrder, default);
        await service.DeleteAsync(empty.Id, default);
        Check((await restored.LoadGroupsAsync(default)).Single().Resources.Count == 3, "빈 그룹 삭제 또는 자원 재등록 실패");

        // Windows 파일 공유 제한으로 실제 저장 교체 실패를 유도하고 원본 보존을 확인한다.
        var before = await System.IO.File.ReadAllTextAsync(path);
        using (var file = new System.IO.FileStream(path, System.IO.FileMode.Open,
            System.IO.FileAccess.Read, System.IO.FileShare.Read))
        {
            var remaining = (await store.LoadGroupsAsync(default)).Single();
            try
            {
                await service.DeleteAsync(remaining.Id, default);
                throw new Exception("저장 실패가 감지되지 않음");
            }
            catch (AppSettingsException) { }
        }
        Check(await System.IO.File.ReadAllTextAsync(path) == before, "저장 실패 후 원본 변경");
    }
    finally
    {
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
    }
}

static async Task DeleteViewModelAsync()
{
    var group = Group();
    var store = new FakeGroupStore { Groups = [group] };
    var service = new ServiceGroupService(store);
    var navigation = new NavigationService();
    var query = new FakeQuery();
    var status = new GroupStatusService(Connection(), query);
    var confirmation = new FakeConfirmation { Result = false };
    var detail = new GroupDetailViewModel(navigation, service, status, confirmation);
    var dashboard = new DashboardViewModel(navigation, service, status);
    navigation.Register(AppPage.Dashboard, dashboard);
    navigation.Register(AppPage.GroupDetail, detail);
    navigation.NavigateTo(AppPage.GroupDetail);
    await dashboard.LoadAsync();
    await detail.LoadAsync(group.Id);
    await detail.DeleteAsync();
    Check(detail.HasGroup && store.Groups.Count == 1, "취소 후 그룹 유실");
    confirmation.Result = true;
    store.FailDelete = true;
    try { await detail.DeleteAsync(); }
    catch (AppSettingsException) { }
    Check(detail.HasGroup && dashboard.Groups.Count == 1 && !detail.IsDeleting,
        "실패 후 화면 또는 삭제 상태 오류");
    store.FailDelete = false;
    await detail.DeleteAsync();
    Check(!detail.HasGroup && detail.CurrentGroupId is null && dashboard.IsEmpty,
        "삭제 후 상세/카드 상태 잔존");
    Check(navigation.CurrentPage == AppPage.Dashboard && query.Calls == 0, "미연결 삭제가 AWS 조회를 요구함");
}

static async Task DeleteDuringQueryAsync()
{
    var connection = Connection();
    await connection.ConnectAsync("first", default);
    var group = Group() with { Resources = [new(AwsResourceKind.Ec2, "i-1", "웹")],
        StartOrder = [new(AwsResourceKind.Ec2, "i-1")] };
    var store = new FakeGroupStore { Groups = [group] };
    var service = new ServiceGroupService(store);
    var response = new TaskCompletionSource<IReadOnlyList<Ec2InstanceResource>>();
    var query = new FakeQuery { Ec2 = (_, _) => response.Task };
    var status = new GroupStatusService(connection, query);
    var navigation = new NavigationService();
    var detail = new GroupDetailViewModel(navigation, service, status, new FakeConfirmation());
    var dashboard = new DashboardViewModel(navigation, service, status);
    navigation.Register(AppPage.Dashboard, dashboard);
    navigation.Register(AppPage.GroupDetail, detail);
    navigation.NavigateTo(AppPage.GroupDetail);
    var cardLoad = dashboard.LoadAsync();
    var detailLoad = detail.LoadAsync(group.Id);
    Check(detail.IsLoading && detail.HasGroup, "조회 대기 상태 구성 실패");
    await detail.DeleteAsync();
    response.SetResult([new("i-1", "웹", "t3.micro", "running")]);
    await Task.WhenAll(cardLoad, detailLoad);
    Check(!detail.HasGroup && detail.Resources.Count == 0 && dashboard.Groups.Count == 0,
        "늦은 조회가 삭제 그룹을 다시 반영함");
}

static ServiceGroup Group(string account = "111111111111") => new(
    Guid.NewGuid(), "테스트 그룹", account, "ap-northeast-2", "first",
    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
    [new GroupResource(AwsResourceKind.Ec2, "i-1", "웹"),
     new GroupResource(AwsResourceKind.Rds, "db-1", "DB"),
     new GroupResource(AwsResourceKind.S3, "bucket-1", "버킷", "us-east-1")],
    [new GroupResourceRef(AwsResourceKind.Ec2, "i-1"),
     new GroupResourceRef(AwsResourceKind.Rds, "db-1")]);

static AwsConnectionService Connection() => new(new FakeProfiles(), new FakeSessionFactory(), new ControlActivityState());

static async Task AccessGateAsync()
{
    var connection = Connection();
    var query = new FakeQuery();
    var service = new GroupStatusService(connection, query);
    var missing = await service.ReadAsync(Group(), default);
    Check(missing.AccessMessage?.Contains("미연결") == true, "미연결 안내 누락");
    Check(query.Calls == 0, "미연결 상태에서 AWS 조회 시도");
    var empty = await service.ReadAsync(Group() with { Resources = [], StartOrder = [] }, default);
    Check(empty.AccessMessage is null && empty.Services.Count == 0 && query.Calls == 0,
        "빈 그룹이 불필요한 AWS 연결 또는 조회를 요구함");

    await connection.ConnectAsync("first", default);
    var mismatch = await service.ReadAsync(Group("222222222222"), default);
    Check(mismatch.AccessMessage?.Contains("계정 불일치") == true, "계정 불일치 안내 누락");
    Check(query.Calls == 0, "다른 계정의 AWS 조회 시도");
}

static async Task SuccessAndEmptyAsync()
{
    var connection = Connection();
    await connection.ConnectAsync("first", default);
    var query = new FakeQuery
    {
        Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>(
            [new("i-1", "웹", "t3.micro", "running")]),
        Rds = (_, _) => Task.FromResult<IReadOnlyList<RdsInstanceResource>>([]),
        S3 = _ => Task.FromResult<IReadOnlyList<S3BucketResource>>([new("bucket-1", "us-east-1")])
    };
    var group = Group();
    var result = await new GroupStatusService(connection, query).ReadAsync(group, default);
    Check(result.AccessMessage is null && result.Services.Count == 3, "서비스별 정상 결과 누락");
    Check(result.Services.Single(x => x.Kind == AwsResourceKind.Ec2).States["i-1"] == "running", "EC2 상태 오류");
    Check(result.Services.Single(x => x.Kind == AwsResourceKind.Rds).States.Count == 0, "빈 RDS 결과 오류");
    Check(result.Services.Single(x => x.Kind == AwsResourceKind.S3).States["bucket-1"] == "조회 가능", "타 리전 S3 조회 오류");

    var card = new ServiceGroupCard(group);
    card.Apply(result);
    Check(card.Statuses.Single(x => x.Kind == AwsResourceKind.Ec2).Summary.Contains("실행 1"), "카드 실행 상태 누락");
    Check(card.Statuses.Single(x => x.Kind == AwsResourceKind.Rds).Summary.Contains("확인 불가 1"), "빈 목록을 중지 상태로 오인");
    Check(card.LastSuccessText != "마지막 정상 조회: 없음", "정상 조회 시각 누락");
}

static async Task IndependentFailuresAsync()
{
    var connection = Connection();
    await connection.ConnectAsync("first", default);
    var query = new FakeQuery
    {
        Ec2 = (_, _) => Task.FromException<IReadOnlyList<Ec2InstanceResource>>(
            new AwsResourceQueryException(AwsQueryFailureKind.AccessDenied, "EC2 권한 없음")),
        Rds = (_, _) => Task.FromResult<IReadOnlyList<RdsInstanceResource>>(
            [new("db-1", "postgres", "available")]),
        S3 = _ => Task.FromException<IReadOnlyList<S3BucketResource>>(
            new AwsResourceQueryException(AwsQueryFailureKind.Network, "S3 통신 실패"))
    };
    var group = Group();
    var service = new GroupStatusService(connection, query);
    var result = await service.ReadAsync(group, default);
    Check(result.Services.Count == 3, "서비스 오류가 다른 서비스 조회를 중단함");
    Check(result.Services.Single(x => x.Kind == AwsResourceKind.Ec2).Error == "EC2 권한 없음", "권한 오류 누락");
    Check(result.Services.Single(x => x.Kind == AwsResourceKind.Rds).States["db-1"] == "available", "RDS 정상 결과 손실");
    Check(result.Services.Single(x => x.Kind == AwsResourceKind.S3).Error == "S3 통신 실패", "네트워크 오류 누락");

    var card = new ServiceGroupCard(group);
    card.Apply(new GroupReadResult(null,
        [new ServiceReadResult(AwsResourceKind.Ec2,
            new Dictionary<string, string> { ["i-1"] = "running" }, DateTimeOffset.UtcNow, null)]));
    card.Apply(result);
    var ec2 = card.Statuses.Single(x => x.Kind == AwsResourceKind.Ec2);
    Check(ec2.Summary.Contains("이전 상태") && ec2.Summary.Contains("실행 1"), "실패 시 마지막 정상 상태 손실");
    Check(ec2.Detail.Contains("마지막 정상 조회"), "이전 정상 시각 누락");
}

static async Task ExpiredSessionAsync()
{
    var connection = Connection();
    await connection.ConnectAsync("first", default);
    var query = new FakeQuery
    {
        Ec2 = (_, _) => Task.FromException<IReadOnlyList<Ec2InstanceResource>>(
            new AwsResourceQueryException(AwsQueryFailureKind.ExpiredSession, "세션 만료"))
    };
    var result = await new GroupStatusService(connection, query).ReadAsync(Group(), default);
    Check(connection.RequiresReconnect && !connection.IsConnected, "만료된 연결을 계속 사용");
    Check(result.AccessMessage?.Contains("재연결") == true, "재연결 안내 누락");
    Check(query.Calls == 1, "만료 후 추가 AWS 조회 시도");
}

static async Task ConnectionFailuresAsync()
{
    var factory = new FakeSessionFactory();
    var connection = new AwsConnectionService(new FakeProfiles(), factory, new ControlActivityState());
    await connection.ConnectAsync("first", default);
    try
    {
        await connection.ConnectAsync("missing", default);
        throw new Exception("없는 프로필 연결이 성공함");
    }
    catch (AwsConnectionException ex) when (ex.Kind == AwsConnectionFailureKind.ProfileMissing) { }
    Check(connection.IsConnected && connection.ActiveConnection?.ProfileName == "first",
        "새 프로필 실패 후 기존 연결 손실");

    factory.LastSession!.ExpireOnVerify = true;
    try
    {
        await connection.VerifyAsync(default);
        throw new Exception("만료된 STS 재확인이 성공함");
    }
    catch (AwsConnectionException ex) when (ex.Kind == AwsConnectionFailureKind.ExpiredSession) { }
    Check(connection.RequiresReconnect && !connection.IsConnected, "STS 만료 후 AWS 요청 차단 실패");
}

static async Task LateResponseAsync()
{
    var connection = Connection();
    await connection.ConnectAsync("first", default);
    var delayed = new TaskCompletionSource<IReadOnlyList<Ec2InstanceResource>>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var query = new FakeQuery { Ec2 = (_, _) => delayed.Task };
    var read = new GroupStatusService(connection, query).ReadAsync(Group(), default);
    Check(query.Calls == 1, "지연 조회 시작 실패");
    connection.Disconnect();
    delayed.SetResult([new("i-1", "웹", "t3.micro", "running")]);
    var result = await read;
    Check(result.AccessMessage is not null && result.Services.Count == 0, "이전 연결 결과를 화면에 반영");
    Check(query.Calls == 1, "연결 해제 후 추가 AWS 조회 시도");
}

static async Task QueryBoundaryAsync()
{
    var factory = new FakeSessionFactory();
    var connection = new AwsConnectionService(new FakeProfiles(), factory, new ControlActivityState());
    await connection.ConnectAsync("first", default);
    var delayed = new TaskCompletionSource<IReadOnlyList<Ec2InstanceResource>>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    factory.LastSession!.Ec2 = (_, _) => delayed.Task;
    var read = new AwsResourceQueryService(connection).ListEc2Async("ap-northeast-2", default);
    connection.Disconnect();
    delayed.SetResult([new("i-1", "웹", "t3.micro", "running")]);
    try
    {
        _ = await read;
        throw new Exception("이전 세션의 늦은 조회가 성공함");
    }
    catch (AwsConnectionException ex) when (ex.Kind == AwsConnectionFailureKind.Busy) { }
}

static async Task WizardQueryPathsAsync()
{
    var connection = Connection();
    await connection.ConnectAsync("first", default);
    var query = new FakeQuery
    {
        Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>(
            [new("i-1", "웹", "t3.micro", "running")])
    };
    var wizard = new GroupWizardViewModel(connection, query,
        new ServiceGroupService(new FakeGroupStore()), new NavigationService(), new FakeConfirmation(), _ => { });
    wizard.GroupName = "조회 경로";
    wizard.NextCommand.Execute(null);
    await UntilAsync(() => wizard.Step == 2);
    wizard.IncludeEc2 = true;
    wizard.NextCommand.Execute(null);
    await UntilAsync(() => wizard.Step == 3);

    wizard.QueryEc2Command.Execute(null);
    await UntilAsync(() => wizard.Ec2Status.Contains("조회 완료"));
    Check(wizard.Ec2Choices.Count == 1, "정상 EC2 목록 누락");

    query.Ec2 = (_, _) => Task.FromException<IReadOnlyList<Ec2InstanceResource>>(
        new AwsResourceQueryException(AwsQueryFailureKind.AccessDenied, "EC2 권한 없음"));
    wizard.QueryEc2Command.Execute(null);
    await UntilAsync(() => wizard.Ec2Status.Contains("조회 실패"));
    Check(wizard.Ec2Choices.Count == 1 && wizard.Ec2Status.Contains("이전 목록 유지"),
        "권한 오류 시 이전 목록 손실");

    query.Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>([]);
    wizard.QueryEc2Command.Execute(null);
    await UntilAsync(() => wizard.Ec2Status.Contains("리소스 없음"));
    Check(wizard.Ec2Choices.Count == 0, "정상 빈 결과가 이전 목록을 유지함");

    var delayed = new TaskCompletionSource<IReadOnlyList<Ec2InstanceResource>>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    query.Ec2 = (_, _) => delayed.Task;
    wizard.QueryEc2Command.Execute(null);
    await UntilAsync(() => wizard.IsEc2Loading);
    connection.Disconnect();
    delayed.SetResult([new("i-late", "늦은 응답", "t3.micro", "running")]);
    await UntilAsync(() => !wizard.IsEc2Loading);
    Check(wizard.Ec2Choices.Count == 0 && wizard.Ec2Status.Contains("조회 실패"),
        "연결 변경 후 늦은 결과가 선택 목록에 반영됨");
}

static async Task UntilAsync(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline) throw new Exception("비동기 명령 완료 대기 시간 초과");
        await Task.Delay(10);
    }
}

static Task WpfViewsAsync()
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            var app = new PersonalServerManager.App();
            app.InitializeComponent();
            // 전역 하단 단계 목록과 취소 버튼의 XAML 리소스도 화면 생성 시 확인한다.
            var window = new PersonalServerManager.MainWindow();
            window.Measure(new System.Windows.Size(1440, 900));
            window.Arrange(new System.Windows.Rect(0, 0, 1440, 900));
            window.UpdateLayout();
            var navigation = new NavigationService();
            var group = Group();
            var groups = new ServiceGroupService(new FakeGroupStore { Groups = [group] });
            var activity = new ControlActivityState();
            var connection = new AwsConnectionService(new FakeProfiles(), new FakeSessionFactory(), activity);
            connection.ConnectAsync("first", default).GetAwaiter().GetResult();
            var query = new FakeQuery
            {
                Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>([new("i-1", "웹 서버", "t3.micro", "stopped")])
            };
            var status = new GroupStatusService(connection, query);
            var control = new ResourceControlService(connection, groups, activity, () => new ControlTimeoutSettings());
            var detail = new GroupDetailViewModel(navigation, groups, status, new FakeConfirmation(), control);
            detail.LoadAsync(group.Id).GetAwaiter().GetResult();
            detail.SelectedResource = detail.Resources.First(row => row.Kind == "EC2");
            var settings = new SettingsViewModel(new JsonAppSettingsStore(), new FakeConfirmation(), new ControlTimeoutSettings());
            foreach (var view in new System.Windows.Controls.UserControl[]
                     { new DashboardView(), new GroupDetailView { DataContext = detail }, new SettingsView { DataContext = settings } })
            {
                view.Measure(new System.Windows.Size(1280, 1100));
                view.Arrange(new System.Windows.Rect(0, 0, 1280, 1100));
                view.UpdateLayout();
                view.Measure(new System.Windows.Size(1280, 1100));
                view.Arrange(new System.Windows.Rect(0, 0, 1280, 1100));
                view.UpdateLayout();
                if (view is GroupDetailView or SettingsView)
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 1100, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var image = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, $"{view.GetType().Name}-preview.png"));
                    encoder.Save(image);
                }
            }
            completion.SetResult();
        }
        catch (Exception exception) { completion.SetException(exception); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeProfiles : IAwsProfileProvider
{
    public Task<IReadOnlyList<string>> ListProfilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["first", "other"]);
}

/// <summary>분 단위 제한시간을 실제 대기 없이 진행시키는 제어 테스트 시계다.</summary>
sealed class ManualControlClock : TimeProvider
{
    private readonly List<ClockTimer> _timers = [];
    private TimeSpan _elapsed;
    public int PendingTimers { get { lock (_timers) return _timers.Count(timer => !timer.Disposed); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_timers)
        {
            var timer = new ClockTimer(this, callback, state, _elapsed + dueTime);
            _timers.Add(timer);
            return timer;
        }
    }

    public void Advance(TimeSpan duration)
    {
        ClockTimer[] ready;
        lock (_timers)
        {
            _elapsed += duration;
            ready = _timers.Where(timer => !timer.Disposed && timer.Due <= _elapsed).ToArray();
            foreach (var timer in ready) timer.Disposed = true;
        }
        foreach (var timer in ready) timer.Fire();
    }

    private sealed class ClockTimer(ManualControlClock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; private set; } = due;
        public bool Disposed { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                if (Disposed) return false;
                Due = clock._elapsed + dueTime;
                return true;
            }
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (clock._timers) Disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

sealed class FakeSessionFactory : IAwsProfileSessionFactory
{
    public FakeSession? LastSession { get; private set; }
    public Task<IAwsProfileSession> CreateAsync(string profileName, CancellationToken cancellationToken)
    {
        if (profileName == "missing")
            throw new AwsConnectionException(AwsConnectionFailureKind.ProfileMissing, "프로필 없음");
        LastSession = new FakeSession(profileName);
        return Task.FromResult<IAwsProfileSession>(LastSession);
    }
}

sealed class FakeSession(string profileName) : IAwsProfileSession, IAwsResourceControlSession
{
    public int Sent { get; private set; }
    public List<(AwsResourceKind Kind, string Id, ResourceControlAction Action)> SentCommands { get; } = [];
    public Func<string, AwsResourceKind, string, CancellationToken, Task<ResourceControlSnapshot>> ControlState { get; set; } =
        (_, _, _, _) => Task.FromResult(new ResourceControlSnapshot("stopped"));
    public Func<string, AwsResourceKind, string, ResourceControlAction, CancellationToken, Task> Send { get; set; } =
        (_, _, _, _, _) => Task.CompletedTask;
    public Task<ResourceControlSnapshot> ReadControlStateAsync(string region, AwsResourceKind kind, string id, CancellationToken token) =>
        ControlState(region, kind, id, token);
    public Task SendControlAsync(string region, AwsResourceKind kind, string id, ResourceControlAction action, CancellationToken token)
    {
        Sent++;
        SentCommands.Add((kind, id, action));
        return Send(region, kind, id, action, token);
    }
    public bool ExpireOnVerify { get; set; }
    public Func<string, CancellationToken, Task<IReadOnlyList<Ec2InstanceResource>>> Ec2 { get; set; } =
        (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>([]);
    public Task<AwsIdentity> GetCallerIdentityAsync(CancellationToken cancellationToken) =>
        ExpireOnVerify
            ? Task.FromException<AwsIdentity>(new AwsConnectionException(AwsConnectionFailureKind.ExpiredSession, "STS 만료"))
            : Task.FromResult(new AwsIdentity(profileName == "first" ? "111111111111" : "222222222222", "arn:aws:iam::test:user/mock"));
    public Task<IReadOnlyList<Ec2InstanceResource>> ListEc2InstancesAsync(string region, CancellationToken cancellationToken) =>
        Ec2(region, cancellationToken);
    public Task<IReadOnlyList<RdsInstanceResource>> ListRdsInstancesAsync(string region, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RdsInstanceResource>>([]);
    public Task<IReadOnlyList<S3BucketResource>> ListS3BucketsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<S3BucketResource>>([]);
}

sealed class FakeQuery : IAwsResourceQuery
{
    public int Calls { get; private set; }
    public Func<string, CancellationToken, Task<IReadOnlyList<Ec2InstanceResource>>> Ec2 { get; set; } =
        (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>([]);
    public Func<string, CancellationToken, Task<IReadOnlyList<RdsInstanceResource>>> Rds { get; set; } =
        (_, _) => Task.FromResult<IReadOnlyList<RdsInstanceResource>>([]);
    public Func<CancellationToken, Task<IReadOnlyList<S3BucketResource>>> S3 { get; set; } =
        _ => Task.FromResult<IReadOnlyList<S3BucketResource>>([]);
    public Task<IReadOnlyList<Ec2InstanceResource>> ListEc2Async(string region, CancellationToken token)
    { Calls++; return Ec2(region, token); }
    public Task<IReadOnlyList<RdsInstanceResource>> ListRdsAsync(string region, CancellationToken token)
    { Calls++; return Rds(region, token); }
    public Task<IReadOnlyList<S3BucketResource>> ListS3Async(CancellationToken token)
    { Calls++; return S3(token); }
}

sealed class FakeGroupStore : IServiceGroupStore
{
    public List<ServiceGroup> Groups { get; set; } = [];
    public bool FailDelete { get; set; }
    public Task<IReadOnlyList<ServiceGroup>> LoadGroupsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServiceGroup>>(Groups.ToArray());
    public Task AddGroupAsync(ServiceGroup group, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken)
    {
        if (FailDelete) throw new AppSettingsException("테스트 저장 실패");
        Groups.RemoveAll(group => group.Id == groupId);
        return Task.CompletedTask;
    }
}

sealed class FakeConfirmation : IConfirmationService
{
    public bool Result { get; set; } = true;
    public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText,
        CancellationToken cancellationToken = default) => Task.FromResult(Result);
}
