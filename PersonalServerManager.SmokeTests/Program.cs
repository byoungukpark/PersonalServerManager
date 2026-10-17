using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Models;
using PersonalServerManager.Application.Services;
using PersonalServerManager.Domain.Models;
using PersonalServerManager.Presentation.Dialogs;
using PersonalServerManager.Presentation.Navigation;
using PersonalServerManager.Presentation.ViewModels;
using PersonalServerManager.Presentation.Views;

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
    ("대시보드·그룹 상세 WPF 화면 생성", WpfViewsAsync)
};

foreach (var (name, run) in checks)
{
    await run();
    Console.WriteLine($"PASS {name}");
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
            foreach (var view in new System.Windows.Controls.UserControl[]
                     { new DashboardView(), new GroupDetailView() })
            {
                view.Measure(new System.Windows.Size(1200, 800));
                view.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
                view.UpdateLayout();
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

sealed class FakeSession(string profileName) : IAwsProfileSession
{
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
    public Task<IReadOnlyList<ServiceGroup>> LoadGroupsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ServiceGroup>>([]);
    public Task AddGroupAsync(ServiceGroup group, CancellationToken cancellationToken) => Task.CompletedTask;
}

sealed class FakeConfirmation : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
}
