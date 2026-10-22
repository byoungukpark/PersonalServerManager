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
    ("그룹 편집 확인·자원 누락·저장 복원", GroupEditingPersistenceAsync),
    ("그룹 편집 중복·계정·저장 실패 차단", GroupEditingGuardsAsync),
    ("대시보드·그룹 상세 WPF 화면 생성", WpfViewsAsync),
    ("그룹 그래프 화면 이탈·재진입", MetricReentryViewAsync),
    ("CloudWatch 그룹 카드·범위·오류·취소", MetricMonitoringAsync),
    ("30초 상태 결과 상세 화면 공유", SharedStatusAsync)
};

foreach (var (name, run) in checks.Where(check => args.Length == 0 || check.Name.Contains(args[0], StringComparison.OrdinalIgnoreCase)))
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

static async Task GroupEditingPersistenceAsync()
{
    var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"psm-edit-{Guid.NewGuid():N}");
    var path = System.IO.Path.Combine(directory, "settings.json");
    try
    {
        var store = new JsonAppSettingsStore(path);
        var sample = Group();
        var original = sample with
        {
            Resources = sample.Resources.Select(resource => resource.Kind == AwsResourceKind.S3
                ? resource : resource with { Region = sample.Region }).ToArray()
        };
        await store.SaveAsync("first", default);
        await store.AddGroupAsync(original, default);
        var activity = new ControlActivityState();
        var factory = new FakeSessionFactory();
        var connection = new AwsConnectionService(new FakeProfiles(), factory, activity);
        await connection.ConnectAsync("first", default);
        var groups = new ServiceGroupService(store, controlActivity: activity, connection: connection);
        var query = new FakeQuery
        {
            Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>(
                [new("i-2", "새 서버", "t3.micro", "stopped")])
        };
        var confirmation = new FakeConfirmation { Result = false };
        var edit = new GroupManagementViewModel(groups, connection, query, confirmation);
        await edit.LoadAsync(original.Id);
        await edit.QueryResourcesAsync(AwsResourceKind.Ec2);
        Check(edit.Ec2Choices.Single(item => item.Id == "i-1").IsSelected &&
            edit.Ec2Choices.Single(item => item.Id == "i-1").State.Contains("찾을 수 없음"),
            "AWS 목록에서 빠진 등록 자원을 자동 연결 해제함");
        edit.Name = "새 그룹 이름";
        edit.S3Choices.Single().IsSelected = false;
        edit.Ec2Choices.Single(item => item.Id == "i-2").IsSelected = true;
        edit.MoveOrder(edit.StartOrder.Single(item => item.Id == "i-2"),
            edit.StartOrder.Single(item => item.Id == "db-1"));
        Check(edit.HasChanges && !await edit.ConfirmLeaveAsync(), "미저장 이탈 확인 누락");
        await edit.SaveAsync();
        Check((await store.LoadGroupsAsync(default)).Single().Name == original.Name,
            "저장 확인 취소 후 JSON 변경");
        confirmation.Result = true;
        await edit.SaveAsync();
        var saved = (await new JsonAppSettingsStore(path).LoadGroupsAsync(default)).Single();
        Check(saved.Id == original.Id && saved.AccountId == original.AccountId &&
            saved.Region == original.Region && saved.Name == "새 그룹 이름" &&
            saved.Resources.Any(item => item.Id == "i-1") &&
            saved.Resources.Any(item => item.Id == "i-2") &&
            saved.Resources.All(item => item.Kind != AwsResourceKind.S3) &&
            saved.StartOrder.Select(item => item.Id).SequenceEqual(["i-1", "i-2", "db-1"]) &&
            await new JsonAppSettingsStore(path).LoadAsync(default) == "first" &&
            factory.LastSession!.Sent == 0 && !edit.HasChanges,
            "그룹 편집 저장·복원 또는 AWS 비제어 보장 실패");
    }
    finally
    {
        if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true);
    }
}

static async Task GroupEditingGuardsAsync()
{
    var activity = new ControlActivityState();
    var factory = new FakeSessionFactory();
    var connection = new AwsConnectionService(new FakeProfiles(), factory, activity);
    await connection.ConnectAsync("first", default);
    var original = Group();
    var other = Group() with
    {
        Name = "다른 그룹",
        Resources = [new GroupResource(AwsResourceKind.Ec2, "i-other", "다른 서버", "ap-northeast-2")],
        StartOrder = [new GroupResourceRef(AwsResourceKind.Ec2, "i-other")]
    };
    var store = new FakeGroupStore { Groups = [original, other] };
    var groups = new ServiceGroupService(store, controlActivity: activity, connection: connection);
    try
    {
        await groups.UpdateAsync(original, other.Name, original.Resources, original.StartOrder, null, default);
        throw new Exception("중복 그룹 이름 저장 허용");
    }
    catch (ServiceGroupException) { }
    try
    {
        await groups.UpdateAsync(original, "새 이름", [.. original.Resources, other.Resources[0]],
            [.. original.StartOrder, other.StartOrder[0]], connection.ActiveConnection!.ConnectionVersion, default);
        throw new Exception("다른 그룹 자원 중복 연결 허용");
    }
    catch (ServiceGroupException) { }
    await connection.ConnectAsync("other", default);
    try
    {
        await groups.UpdateAsync(original, "새 이름",
            [.. original.Resources, new GroupResource(AwsResourceKind.Ec2, "i-2", "새 서버", original.Region)],
            [.. original.StartOrder, new GroupResourceRef(AwsResourceKind.Ec2, "i-2")],
            connection.ActiveConnection!.ConnectionVersion, default);
        throw new Exception("다른 계정으로 새 자원 연결 허용");
    }
    catch (ServiceGroupException) { }

    store.FailReplace = true;
    try
    {
        await groups.UpdateAsync(original, "저장 실패 이름", original.Resources, original.StartOrder, null, default);
        throw new Exception("저장 실패를 성공으로 표시");
    }
    catch (AppSettingsException) { }
    Check(store.Groups[0].Name == original.Name, "저장 실패 후 기존 그룹 훼손");
    store.FailReplace = false;
    var changed = await groups.UpdateAsync(original, "변경 완료", original.Resources, original.StartOrder, null, default);
    try
    {
        await groups.UpdateAsync(original, "오래된 초안", original.Resources, original.StartOrder, null, default);
        throw new Exception("오래된 그룹 초안으로 최신 저장 덮어씀");
    }
    catch (ServiceGroupException) { }
    Check(store.Groups[0] == changed && factory.LastSession!.Sent == 0, "편집 충돌 또는 AWS 비제어 오류");
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

static async Task MetricMonitoringAsync()
{
    var activity = new ControlActivityState();
    var factory = new FakeSessionFactory();
    var connection = new AwsConnectionService(new FakeProfiles(), factory, activity);
    await connection.ConnectAsync("first", default);
    var session = factory.LastSession!;
    var group = Group();
    var query = new AwsCloudWatchMetricQuery(connection);
    var calls = new List<(AwsResourceKind Kind, TimeSpan Range, string Region)>();
    session.Metrics = (resource, range, _) =>
    {
        calls.Add((resource.Kind, range, resource.Region));
        return Task.FromResult(MetricSample(resource.Kind));
    };

    try
    {
        await query.ReadAsync(group with { AccountId = "222222222222" }, group.Resources[0], TimeSpan.FromHours(1), default);
        throw new Exception("다른 계정의 CloudWatch 조회를 허용함");
    }
    catch (AwsConnectionException) { }
    Check(calls.Count == 0, "계정 게이트 앞에서 AWS 지표 요청");

    var navigation = new NavigationService();
    var groups = new ServiceGroupService(new FakeGroupStore { Groups = [group] });
    var status = new GroupStatusService(connection, new FakeQuery());
    var detail = new GroupDetailViewModel(navigation, groups, status, new FakeConfirmation(), null, query);
    navigation.Register(AppPage.GroupDetail, detail);
    navigation.Register(AppPage.Dashboard, new DashboardViewModel(navigation, groups, status));
    navigation.NavigateTo(AppPage.GroupDetail);
    await detail.LoadAsync(group.Id);
    detail.SelectedContentTabIndex = 1;
    await UntilAsync(() => detail.Monitoring.Cards.All(card => card.Charts.Count ==
        (card.Resource.Kind == AwsResourceKind.Ec2 ? 4 : card.Resource.Kind == AwsResourceKind.Rds ? 6 : 2)));
    Check(calls.Count == 3 && calls.Any(call => call.Kind == AwsResourceKind.S3 && call.Region == "us-east-1") &&
          calls.Any(call => call.Kind == AwsResourceKind.Ec2 && call.Region == group.Region),
        "그룹 전체 자원 또는 리전별 CloudWatch 조회 오류");
    var ec2 = detail.Monitoring.Cards.Single(card => card.Resource.Kind == AwsResourceKind.Ec2);
    var rds = detail.Monitoring.Cards.Single(card => card.Resource.Kind == AwsResourceKind.Rds);
    var s3 = detail.Monitoring.Cards.Single(card => card.Resource.Kind == AwsResourceKind.S3);
    Check(!ReferenceEquals(s3.Charts[1].PlotFactory!(), s3.Charts[1].PlotFactory!()),
        "같은 그래프 카드가 PlotModel을 재사용함");
    Check(ec2.Charts[0].LatestText.Contains("35") && ec2.Charts[0].MeasuredAtText.Contains("AWS 측정 시각"),
        "최신 지표·측정 시각 누락");
    var statusPlot = ec2.Charts[3].PlotFactory!();
    Check(ec2.Charts[3].StatusBadge == "정상" &&
          statusPlot.Series[0] is OxyPlot.Series.StairStepSeries steps &&
          steps.Points[0].Y == 1 && steps.Points[1].Y == 0 &&
          statusPlot.Axes.All(axis => !axis.IsPanEnabled && !axis.IsZoomEnabled) &&
          rds.Charts[4].LatestText.Contains("ms"), "상태 검사 계단형 그래프 또는 RDS 지연 단위 누락");

    detail.Monitoring.SelectedLayout = detail.Monitoring.MetricLayouts[1];
    Check(detail.Monitoring.ChartColumns == 1 && calls.Count == 3,
        "스택 전환이 카드 배치 외 지표 조회에 영향을 줌");
    detail.Monitoring.SelectedLayout = detail.Monitoring.MetricLayouts[0];
    Check(detail.Monitoring.ChartColumns == 2 && calls.Count == 3,
        "그리드 전환이 지표를 다시 요청함");

    detail.SelectedResource = detail.Resources.Single(row => row.Kind == "RDS");
    Check(calls.Count == 3 && ec2.Charts.Count == 4, "개별 제어 리소스 선택이 그룹 지표 조회에 영향을 줌");
    detail.Monitoring.SelectedRange = detail.Monitoring.MetricRanges[1];
    await UntilAsync(() => calls.Count == 5 && !detail.Monitoring.IsRefreshing);
    Check(calls.Where(call => call.Kind != AwsResourceKind.S3).All(call =>
        call.Range == TimeSpan.FromHours(1) || call.Range == TimeSpan.FromHours(3)),
        "시간 범위 변경 뒤 EC2/RDS 재조회 오류");
    Check(calls.Count(call => call.Kind == AwsResourceKind.S3) == 1, "범위 변경 때 S3 캐시를 다시 조회함");

    session.Metrics = (resource, _, _) => resource.Kind == AwsResourceKind.Rds
        ? Task.FromException<IReadOnlyList<MetricSeries>>(
            new AwsResourceQueryException(AwsQueryFailureKind.AccessDenied, "CloudWatch 조회 권한 없음"))
        : Task.FromResult(MetricSample(resource.Kind));
    await detail.Monitoring.RefreshAsync();
    Check(rds.HasError && rds.Charts[1].HasData && rds.StatusMessage.Contains("이전 측정값") &&
          ec2.Charts[0].HasData, "카드별 오류에서 이전값 또는 다른 자원 그래프를 보존하지 않음");

    session.Metrics = (resource, _, _) => Task.FromResult<IReadOnlyList<MetricSeries>>(
        resource.Kind == AwsResourceKind.Ec2
            ? MetricSample(resource.Kind).Select(series => series.Key == "statusCheckFailed"
                ? series with { Points = [new MetricPoint(DateTimeOffset.UtcNow, 1)] }
                : series).ToArray()
            : MetricSample(resource.Kind));
    await detail.Monitoring.RefreshAsync();
    Check(ec2.Charts[3].StatusBadge == "실패" && ec2.Charts[3].IsFailed,
        "상태 검사 최신 실패값을 실패 배지로 표시하지 않음");

    session.Metrics = (resource, _, _) => Task.FromResult<IReadOnlyList<MetricSeries>>(
        resource.Kind == AwsResourceKind.Ec2
            ? [new("cpu", "CPU 사용률", "%", []), new("networkIn", "수신", "B", []),
               new("networkOut", "송신", "B", []), new("statusCheckFailed", "상태 검사", "개", [])]
            : MetricSample(resource.Kind));
    await detail.Monitoring.RefreshAsync();
    Check(ec2.Charts.All(chart => !chart.HasData) && ec2.Charts[3].StatusBadge == "데이터 없음" &&
          ec2.Charts[3].IsMissing && ec2.Charts[3].PlotFactory is null,
        "빈 지표를 0으로 표시함");

    var delayed = new TaskCompletionSource<IReadOnlyList<MetricSeries>>(TaskCreationOptions.RunContinuationsAsynchronously);
    session.Metrics = (resource, _, _) => resource.Kind == AwsResourceKind.Ec2
        ? delayed.Task : Task.FromResult(MetricSample(resource.Kind));
    var pending = detail.Monitoring.RefreshAsync();
    await UntilAsync(() => ec2.IsLoading);
    detail.SelectedContentTabIndex = 0;
    delayed.SetResult(MetricSample(AwsResourceKind.Ec2));
    await pending;
    Check(ec2.Charts.All(chart => !chart.HasData), "탭 이탈 후 늦은 지표가 카드에 반영됨");
    detail.SelectedContentTabIndex = 1;
    navigation.NavigateTo(AppPage.Dashboard);
    navigation.NavigateTo(AppPage.GroupDetail);
    await detail.LoadAsync(group.Id);
    Check(s3.Charts[1].Title == "객체 수" && calls.Count(call => call.Kind == AwsResourceKind.S3) == 1,
        "그룹 상세 재진입 때 S3 캐시 손실");
}
static IReadOnlyList<MetricSeries> MetricSample(AwsResourceKind kind)
{
    var at = DateTimeOffset.UtcNow.AddMinutes(-2);
    var cpu = new MetricSeries("cpu", "CPU 사용률", "%", [new MetricPoint(at, 35)]);
    if (kind == AwsResourceKind.S3)
        return [new MetricSeries("storage", "저장량", "B", [new MetricPoint(at.AddDays(-1), 8192)]),
            new MetricSeries("objects", "객체 수", "개", [new MetricPoint(at.AddDays(-1), 6)])];
    return kind == AwsResourceKind.Ec2
        ? [cpu,
            new MetricSeries("networkIn", "네트워크 수신", "B", [new MetricPoint(at, 4096)]),
            new MetricSeries("networkOut", "네트워크 송신", "B", [new MetricPoint(at, 2048)]),
            new MetricSeries("statusCheckFailed", "상태 검사", "개",
                [new MetricPoint(at.AddMinutes(-1), 1), new MetricPoint(at, 0)])]
        : [cpu, new MetricSeries("connections", "DB 연결 수", "개", [new MetricPoint(at, 4)]),
            new MetricSeries("freeableMemory", "가용 메모리", "B", [new MetricPoint(at, 1048576)]),
            new MetricSeries("freeStorageSpace", "남은 저장 공간", "B", [new MetricPoint(at, 1073741824)]),
            new MetricSeries("readLatency", "읽기 지연", "ms", [new MetricPoint(at, 2.5)]),
            new MetricSeries("writeLatency", "쓰기 지연", "ms", [new MetricPoint(at, 3.5)])];
}

static async Task SharedStatusAsync()
{
    var group = Group();
    var factory = new FakeSessionFactory();
    var connection = new AwsConnectionService(new FakeProfiles(), factory, new ControlActivityState());
    await connection.ConnectAsync("first", default);
    var state = "running";
    var query = new FakeQuery
    {
        Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>(
            [new Ec2InstanceResource("i-1", "웹", "t3.micro", state)])
    };
    var status = new GroupStatusService(connection, query);
    var groups = new ServiceGroupService(new FakeGroupStore { Groups = [group] });
    var navigation = new NavigationService();
    var detail = new GroupDetailViewModel(navigation, groups, status, new FakeConfirmation());
    var dashboard = new DashboardViewModel(navigation, groups, status);
    dashboard.GroupStatusRead += detail.ApplySharedStatus;
    dashboard.GroupStatusRefreshChanged += detail.SetSharedStatusRefreshing;
    navigation.Register(AppPage.GroupDetail, detail);
    navigation.NavigateTo(AppPage.GroupDetail);
    await detail.LoadAsync(group.Id);
    await dashboard.LoadAsync();
    Check(detail.Resources.Single(row => row.Kind == "EC2").Status == "running", "초기 상세 상태 오류");

    state = "stopped";
    var before = query.Calls;
    await dashboard.RefreshStatusesAsync();
    Check(detail.Resources.Single(row => row.Kind == "EC2").Status == "stopped" &&
          query.Calls - before == 3 && !detail.IsStatusRefreshing,
        "대시보드의 1회 조회 결과를 상세에 공유하지 못함");
}

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
            var factory = new FakeSessionFactory();
            var connection = new AwsConnectionService(new FakeProfiles(), factory, activity);
            connection.ConnectAsync("first", default).GetAwaiter().GetResult();
            factory.LastSession!.Metrics = (resource, _, _) => Task.FromResult(MetricSample(resource.Kind));
            var query = new FakeQuery
            {
                Ec2 = (_, _) => Task.FromResult<IReadOnlyList<Ec2InstanceResource>>([new("i-1", "웹 서버", "t3.micro", "stopped")])
            };
            var status = new GroupStatusService(connection, query);
            var control = new ResourceControlService(connection, groups, activity, () => new ControlTimeoutSettings());
            var detail = new GroupDetailViewModel(navigation, groups, status, new FakeConfirmation(), control,
                new AwsCloudWatchMetricQuery(connection));
            navigation.Register(AppPage.GroupDetail, detail);
            navigation.NavigateTo(AppPage.GroupDetail);
            var management = new GroupManagementViewModel(groups, connection, query, new FakeConfirmation());
            detail.LoadAsync(group.Id).GetAwaiter().GetResult();
            management.LoadAsync(group.Id).GetAwaiter().GetResult();
            detail.SelectedResource = detail.Resources.First(row => row.Kind == "EC2");
            detail.SelectedContentTabIndex = 1;
            var settings = new SettingsViewModel(new JsonAppSettingsStore(), new FakeConfirmation(), new ControlTimeoutSettings());
            foreach (var view in new System.Windows.Controls.UserControl[]
                     { new DashboardView(), new GroupDetailView { DataContext = detail },
                       new GroupManagementView { DataContext = management }, new SettingsView { DataContext = settings } })
            {
                if (view is GroupManagementView)
                {
                    // 문제가 발생한 RDS 관리 표를 첫 배치부터 열어 상태 열을 렌더링한다.
                    management.SelectedResourceTabIndex = 1;
                    var tabs = ((System.Windows.Controls.Grid)view.Content).Children
                        .OfType<System.Windows.Controls.TabControl>().Single();
                    tabs.SelectedIndex = 1;
                }
                var width = view is GroupManagementView ? 1100 : 1280;
                var height = view is GroupManagementView ? 560 : view is GroupDetailView ? 1800 : 1100;
                view.Measure(new System.Windows.Size(width, height));
                view.Arrange(new System.Windows.Rect(0, 0, width, height));
                view.UpdateLayout();
                view.Measure(new System.Windows.Size(width, height));
                view.Arrange(new System.Windows.Rect(0, 0, width, height));
                view.UpdateLayout();
                if (view is GroupDetailView or GroupManagementView or SettingsView)
                {
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
                        System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var image = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, $"{view.GetType().Name}-preview.png"));
                    encoder.Save(image);
                }
                if (view is GroupManagementView)
                {
                    Check(view.FindResource("SharedResourceSelectionTemplate") is System.Windows.DataTemplate,
                        "그룹 생성·편집 공통 리소스 선택 표가 로드되지 않았습니다.");
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

static Task MetricReentryViewAsync()
{
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            if (System.Windows.Application.Current is null)
            {
                var app = new PersonalServerManager.App();
                app.InitializeComponent();
            }
            var navigation = new NavigationService();
            var group = Group();
            var groups = new ServiceGroupService(new FakeGroupStore { Groups = [group] });
            var factory = new FakeSessionFactory();
            var connection = new AwsConnectionService(new FakeProfiles(), factory, new ControlActivityState());
            connection.ConnectAsync("first", default).GetAwaiter().GetResult();
            factory.LastSession!.Metrics = (resource, _, _) => Task.FromResult(MetricSample(resource.Kind));
            var detail = new GroupDetailViewModel(navigation, groups,
                new GroupStatusService(connection, new FakeQuery()), new FakeConfirmation(), null,
                new AwsCloudWatchMetricQuery(connection));
            navigation.Register(AppPage.GroupDetail, detail);
            navigation.Register(AppPage.Dashboard, new PlaceholderViewModel("대시보드", ""));
            navigation.NavigateTo(AppPage.GroupDetail);
            detail.LoadAsync(group.Id).GetAwaiter().GetResult();
            detail.SelectedContentTabIndex = 1;
            var first = new GroupDetailView { DataContext = detail };
            first.Measure(new System.Windows.Size(1280, 1800));
            first.Arrange(new System.Windows.Rect(0, 0, 1280, 1800));
            first.UpdateLayout();
            var chartGrid = FindVisualChild<System.Windows.Controls.Primitives.UniformGrid>(first)
                ?? throw new Exception("그래프 카드 배치를 찾지 못함");
            Check(chartGrid.Columns == 2 && chartGrid.ActualWidth > 900,
                "그리드가 두 열로 가용 너비를 사용하지 않음");
            detail.Monitoring.SelectedLayout = detail.Monitoring.MetricLayouts[1];
            first.UpdateLayout();
            Check(chartGrid.Columns == 1 && chartGrid.Children.Count > 0 &&
                  chartGrid.Children[0].RenderSize.Width > 900,
                "스택이 한 열로 가용 너비를 사용하지 않음");
            Check(detail.Monitoring.Cards.Any(card => card.Charts.Any(chart => chart.PlotFactory is not null)),
                "첫 화면에 그래프가 생성되지 않음");

            navigation.NavigateTo(AppPage.Dashboard);
            navigation.NavigateTo(AppPage.GroupDetail);
            var second = new GroupDetailView { DataContext = detail };
            second.Measure(new System.Windows.Size(1280, 1800));
            second.Arrange(new System.Windows.Rect(0, 0, 1280, 1800));
            second.UpdateLayout();
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

static T? FindVisualChild<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
{
    for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
        if (child is T match) return match;
        var nested = FindVisualChild<T>(child);
        if (nested is not null) return nested;
    }
    return null;
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

sealed class FakeSession(string profileName) : IAwsProfileSession, IAwsResourceControlSession, IAwsCloudWatchSession
{
    public Func<GroupResource, TimeSpan, CancellationToken, Task<IReadOnlyList<MetricSeries>>> Metrics { get; set; } =
        (_, _, _) => Task.FromResult<IReadOnlyList<MetricSeries>>([]);
    public Task<IReadOnlyList<MetricSeries>> ReadMetricsAsync(GroupResource resource, TimeSpan range, CancellationToken token) =>
        Metrics(resource, range, token);
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
    public bool FailReplace { get; set; }
    public Task<IReadOnlyList<ServiceGroup>> LoadGroupsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServiceGroup>>(Groups.ToArray());
    public Task AddGroupAsync(ServiceGroup group, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task ReplaceGroupAsync(ServiceGroup group, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken)
    {
        if (FailReplace) throw new AppSettingsException("테스트 저장 실패");
        var index = Groups.FindIndex(item => item.Id == group.Id);
        if (index < 0 || Groups[index].UpdatedAt != expectedUpdatedAt)
            throw new ServiceGroupException("다른 변경이 먼저 저장되었습니다.");
        GroupRules.Validate(group);
        GroupRules.EnsureUnique(group, Groups.Where(item => item.Id != group.Id));
        Groups[index] = group;
        return Task.CompletedTask;
    }
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
