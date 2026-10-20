using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Models;
using PersonalServerManager.Commands;
using PersonalServerManager.Services.Dialogs;
using PersonalServerManager.Navigation;

namespace PersonalServerManager.ViewModels;

public sealed record AwsRegionOption(string DisplayName, string RegionCode);

/// <summary>리소스 조회 결과의 선택 여부와 다른 그룹 등록 여부를 보관한다.</summary>
public sealed class ResourceChoice : ViewModelBase
{
    private bool _isSelected;
    private string? _registeredGroup;

    public ResourceChoice(AwsResourceKind kind, string id, string name, string detail, string state, string region)
        => (Kind, Id, Name, Detail, State, Region) = (kind, id, name, detail, state, region);

    public AwsResourceKind Kind { get; }
    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public string State { get; }
    public string Region { get; }
    public string? RegisteredGroup
    {
        get => _registeredGroup;
        set
        {
            if (!SetProperty(ref _registeredGroup, value)) return;
            if (value is not null) IsSelected = false;
            OnPropertyChanged(nameof(CanSelect));
            OnPropertyChanged(nameof(RegistrationText));
        }
    }
    public bool CanSelect => RegisteredGroup is null;
    public string RegistrationText => RegisteredGroup is null ? "" : $"{RegisteredGroup} 그룹에 등록됨";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value && CanSelect); }
}

public sealed record StartOrderChoice(AwsResourceKind Kind, string Id, string Name)
{
    public string DisplayName => $"{Kind}: {Name} ({Id})";
}

/// <summary>그룹 생성 5단계의 메모리 초안을 관리하고 최종 등록에서만 저장한다.</summary>
public sealed class GroupWizardViewModel : ViewModelBase
{
    private readonly AwsConnectionService _connection;
    private readonly IAwsResourceQuery _queries;
    private readonly ServiceGroupService _groups;
    private readonly NavigationService _navigation;
    private readonly IConfirmationService _confirmation;
    private readonly Action<ServiceGroup> _openGroup;
    private readonly AsyncRelayCommand _next;
    private readonly AsyncRelayCommand _save;
    private readonly AsyncRelayCommand _queryEc2;
    private readonly AsyncRelayCommand _queryRds;
    private readonly AsyncRelayCommand _queryS3;
    private string _groupName = "";
    private AwsRegionOption? _selectedRegion;
    private int _step = 1;
    private bool _includeEc2, _includeRds, _includeS3;
    private bool _isComplete, _dirty, _isSaving, _ec2Loading, _rdsLoading, _s3Loading;
    private bool _ec2HasResult, _rdsHasResult, _s3HasResult;
    private string _ec2Status = "아직 조회하지 않았습니다.";
    private string _rdsStatus = "아직 조회하지 않았습니다.";
    private string _s3Status = "아직 조회하지 않았습니다.";
    private string _message = "";
    private string? _accountId, _profileName;
    private long _connectionVersion;
    private StartOrderChoice? _selectedOrder;
    private ServiceGroup? _createdGroup;
    private int _selectedResourceTabIndex;

    public GroupWizardViewModel(AwsConnectionService connection, IAwsResourceQuery queries,
        ServiceGroupService groups, NavigationService navigation, IConfirmationService confirmation,
        Action<ServiceGroup> openGroup)
    {
        _connection = connection;
        _queries = queries;
        _groups = groups;
        _navigation = navigation;
        _confirmation = confirmation;
        _openGroup = openGroup;
        Regions = [
            new("서울 (ap-northeast-2)", "ap-northeast-2"),
            new("도쿄 (ap-northeast-1)", "ap-northeast-1"),
            new("싱가포르 (ap-southeast-1)", "ap-southeast-1"),
            new("버지니아 북부 (us-east-1)", "us-east-1"),
            new("오하이오 (us-east-2)", "us-east-2"),
            new("오리건 (us-west-2)", "us-west-2")
        ];
        _selectedRegion = Regions[0];
        Ec2Choices = [];
        RdsChoices = [];
        S3Choices = [];
        StartOrder = [];
        _next = new AsyncRelayCommand(NextAsync, () => Step < 5 && !IsLoading && !IsComplete, ShowError);
        _save = new AsyncRelayCommand(SaveAsync, () => Step == 5 && !IsLoading && !IsComplete, ShowError);
        _queryEc2 = new AsyncRelayCommand(QueryEc2Async, () => CanQuery && IncludeEc2 && !IsEc2Loading, ShowError);
        _queryRds = new AsyncRelayCommand(QueryRdsAsync, () => CanQuery && IncludeRds && !IsRdsLoading, ShowError);
        _queryS3 = new AsyncRelayCommand(QueryS3Async, () => CanQuery && IncludeS3 && !IsS3Loading, ShowError);
        PreviousCommand = new RelayCommand(_ => Previous());
        MoveUpCommand = new RelayCommand(_ => MoveSelectedOrder(-1));
        MoveDownCommand = new RelayCommand(_ => MoveSelectedOrder(1));
        OpenGroupCommand = new RelayCommand(_ => { if (CreatedGroup is { } group) _openGroup(group); });
        DashboardCommand = new RelayCommand(_ => _navigation.NavigateTo(AppPage.Dashboard));
        _connection.PropertyChanged += (_, _) => NotifyConnectionChanged();
    }

    public IReadOnlyList<AwsRegionOption> Regions { get; }
    public ObservableCollection<ResourceChoice> Ec2Choices { get; }
    public ObservableCollection<ResourceChoice> RdsChoices { get; }
    public ObservableCollection<ResourceChoice> S3Choices { get; }
    public ObservableCollection<StartOrderChoice> StartOrder { get; }
    public ICommand NextCommand => _next;
    public ICommand SaveCommand => _save;
    public ICommand QueryEc2Command => _queryEc2;
    public ICommand QueryRdsCommand => _queryRds;
    public ICommand QueryS3Command => _queryS3;
    public ICommand PreviousCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand OpenGroupCommand { get; }
    public ICommand DashboardCommand { get; }
    public ServiceGroup? CreatedGroup => _createdGroup;
    public event EventHandler? GroupCreated;
    public bool IsComplete => _isComplete;
    public bool HasUnsavedChanges => _dirty && !IsComplete;
    public bool IsLoading => IsSaving || IsEc2Loading || IsRdsLoading || IsS3Loading;
    public bool CanGoPrevious => Step > 1 && !IsLoading && !IsComplete;
    public bool CanSelectRegion => !IsLoading;
    public bool CanQuery => _connection.IsConnected && IsDraftConnectionCurrent && Step == 3 && !IsSaving;
    public bool IsDraftConnectionCurrent => _connection.IsConnected && _connection.ActiveConnection is { } active &&
        active.AccountId == _accountId && active.ConnectionVersion == _connectionVersion;
    public string ActiveAccountText => _connection.ActiveConnection is { } active
        ? $"{active.AccountId} · {active.ProfileName}"
        : "AWS 연결 관리에서 먼저 프로필을 연결하세요.";
    public string DraftAccountText => _accountId ?? "—";
    public string DraftProfileText => _profileName ?? "—";
    public string RegionText => SelectedRegion?.DisplayName ?? "—";
    public int SelectedCount => SelectedChoices().Count();
    public string SelectedCountText => $"선택한 리소스 {SelectedCount}개 (EC2 {Ec2Choices.Count(x => x.IsSelected)}, RDS {RdsChoices.Count(x => x.IsSelected)}, S3 {S3Choices.Count(x => x.IsSelected)})";
    public string StartOrderSummary => StartOrder.Count == 0 ? "제어할 EC2/RDS 리소스가 없습니다." :
        string.Join(" → ", StartOrder.Select((x, index) => $"{index + 1}. {x.DisplayName}"));
    public string ReviewResources => SelectedCount == 0 ? "등록할 리소스 없음 (빈 그룹)" :
        string.Join("\n", SelectedChoices().Select(x => $"{x.Kind}: {x.Name} ({x.Id}, {x.Region})"));
    public bool IsStep1 => Step == 1 && !IsComplete;
    public bool IsStep2 => Step == 2 && !IsComplete;
    public bool IsStep3 => Step == 3 && !IsComplete;
    public bool IsStep4 => Step == 4 && !IsComplete;
    public bool IsStep5 => Step == 5 && !IsComplete;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool CanMoveUp => SelectedOrder is not null && StartOrder.IndexOf(SelectedOrder) > 0;
    public bool CanMoveDown => SelectedOrder is not null && StartOrder.IndexOf(SelectedOrder) >= 0 && StartOrder.IndexOf(SelectedOrder) < StartOrder.Count - 1;
    public bool HasSelectedServices => IncludeEc2 || IncludeRds || IncludeS3;
    public int SelectedResourceTabIndex { get => _selectedResourceTabIndex; set => SetProperty(ref _selectedResourceTabIndex, value); }

    public string GroupName { get => _groupName; set { if (SetProperty(ref _groupName, value)) _dirty = true; } }
    public AwsRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (!SetProperty(ref _selectedRegion, value)) return;
            _dirty = true;
            ClearChoices(Ec2Choices);
            ClearChoices(RdsChoices);
            Ec2Status = RdsStatus = "아직 조회하지 않았습니다.";
            _ec2HasResult = _rdsHasResult = false;
            OnPropertyChanged(nameof(RegionText));
        }
    }
    public bool IncludeEc2 { get => _includeEc2; set { if (SetProperty(ref _includeEc2, value)) { if (!value) { ClearChoices(Ec2Choices); Ec2Status = "아직 조회하지 않았습니다."; _ec2HasResult = false; } _dirty = true; OnPropertyChanged(nameof(HasSelectedServices)); RaiseCommands(); } } }
    public bool IncludeRds { get => _includeRds; set { if (SetProperty(ref _includeRds, value)) { if (!value) { ClearChoices(RdsChoices); RdsStatus = "아직 조회하지 않았습니다."; _rdsHasResult = false; } _dirty = true; OnPropertyChanged(nameof(HasSelectedServices)); RaiseCommands(); } } }
    public bool IncludeS3 { get => _includeS3; set { if (SetProperty(ref _includeS3, value)) { if (!value) { ClearChoices(S3Choices); S3Status = "아직 조회하지 않았습니다."; _s3HasResult = false; } _dirty = true; OnPropertyChanged(nameof(HasSelectedServices)); RaiseCommands(); } } }
    public StartOrderChoice? SelectedOrder { get => _selectedOrder; set { if (SetProperty(ref _selectedOrder, value)) NotifyOrderChanged(); } }
    public int Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            OnPropertyChanged(nameof(IsStep1)); OnPropertyChanged(nameof(IsStep2));
            OnPropertyChanged(nameof(IsStep3)); OnPropertyChanged(nameof(IsStep4));
            OnPropertyChanged(nameof(IsStep5)); OnPropertyChanged(nameof(CanGoPrevious));
            RaiseCommands();
        }
    }
    public string Ec2Status { get => _ec2Status; private set => SetProperty(ref _ec2Status, value); }
    public string RdsStatus { get => _rdsStatus; private set => SetProperty(ref _rdsStatus, value); }
    public string S3Status { get => _s3Status; private set => SetProperty(ref _s3Status, value); }
    public string Message { get => _message; private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public bool IsSaving { get => _isSaving; private set { if (SetProperty(ref _isSaving, value)) RaiseCommands(); } }
    public bool IsEc2Loading { get => _ec2Loading; private set { if (SetProperty(ref _ec2Loading, value)) RaiseCommands(); } }
    public bool IsRdsLoading { get => _rdsLoading; private set { if (SetProperty(ref _rdsLoading, value)) RaiseCommands(); } }
    public bool IsS3Loading { get => _s3Loading; private set { if (SetProperty(ref _s3Loading, value)) RaiseCommands(); } }

    public Task<bool> ConfirmLeaveAsync(CancellationToken cancellationToken = default)
    {
        if (IsSaving)
        {
            Message = "그룹 등록이 끝난 후 이동하거나 종료하세요.";
            return Task.FromResult(false);
        }
        return !HasUnsavedChanges ? Task.FromResult(true) : _confirmation.ConfirmAsync(
            "그룹 생성 중단", "저장하지 않은 그룹 정보와 리소스 선택이 사라집니다. 이동하시겠습니까?", "이동", cancellationToken);
    }

    public void ReportNavigationError() => Message = "화면 전환 확인을 표시하지 못했습니다. 다시 시도하세요.";

    public void Reset()
    {
        _next.Cancel(); _queryEc2.Cancel(); _queryRds.Cancel(); _queryS3.Cancel();
        _dirty = false; _accountId = null; _profileName = null; _connectionVersion = 0;
        _createdGroup = null; _isComplete = false;
        _groupName = ""; OnPropertyChanged(nameof(GroupName));
        _selectedRegion = Regions[0]; OnPropertyChanged(nameof(SelectedRegion)); OnPropertyChanged(nameof(RegionText));
        _includeEc2 = _includeRds = _includeS3 = false;
        OnPropertyChanged(nameof(IncludeEc2)); OnPropertyChanged(nameof(IncludeRds)); OnPropertyChanged(nameof(IncludeS3));
        OnPropertyChanged(nameof(HasSelectedServices));
        ClearChoices(Ec2Choices); ClearChoices(RdsChoices); ClearChoices(S3Choices);
        StartOrder.Clear(); SelectedOrder = null;
        Ec2Status = RdsStatus = S3Status = "아직 조회하지 않았습니다.";
        _ec2HasResult = _rdsHasResult = _s3HasResult = false;
        Message = ""; Step = 1; _dirty = false;
        OnPropertyChanged(nameof(IsComplete)); OnPropertyChanged(nameof(CreatedGroup));
        OnPropertyChanged(nameof(DraftAccountText)); OnPropertyChanged(nameof(DraftProfileText));
        OnPropertyChanged(nameof(IsStep1)); NotifySelectionChanged();
    }

    /// <summary>드래그 앤 드롭과 이동 버튼이 같은 순서 변경 규칙을 사용한다.</summary>
    public void MoveOrder(StartOrderChoice source, StartOrderChoice destination)
    {
        var from = StartOrder.IndexOf(source);
        var to = StartOrder.IndexOf(destination);
        if (from < 0 || to < 0 || from == to) return;
        StartOrder.Move(from, to); _dirty = true; NotifyOrderChanged();
    }

    private async Task NextAsync(CancellationToken cancellationToken)
    {
        Message = "";
        if (Step == 1)
        {
            string name;
            try { name = GroupRules.NormalizeName(GroupName); }
            catch (ServiceGroupException ex) { Message = ex.Message; return; }
            if (!_connection.IsConnected || _connection.ActiveConnection is not { } active)
            { Message = "AWS 연결 관리에서 프로필을 연결한 뒤 그룹을 만드세요."; return; }
            if (SelectedRegion is null) { Message = "그룹 리전을 선택하세요."; return; }
            var existing = await _groups.ListAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (existing.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            { Message = "같은 이름의 그룹이 이미 있습니다."; return; }
            GroupName = name;
            _accountId = active.AccountId; _profileName = active.ProfileName;
            _connectionVersion = active.ConnectionVersion;
            OnPropertyChanged(nameof(DraftAccountText)); OnPropertyChanged(nameof(DraftProfileText));
        }
        else if (!IsDraftConnectionCurrent)
        { Message = "AWS 연결이 변경되었습니다. 그룹 생성을 다시 시작하세요."; return; }
        if (Step == 2) SelectedResourceTabIndex = IncludeEc2 ? 0 : IncludeRds ? 1 : 2;
        if (Step == 3) SyncStartOrder();
        Step++;
        NotifySelectionChanged();
    }

    private void Previous() { if (CanGoPrevious) { Message = ""; Step--; } }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Message = "";
        if (!IsDraftConnectionCurrent || SelectedRegion is null || _accountId is null || _profileName is null)
        { Message = "AWS 연결이 변경되었습니다. 그룹 생성을 다시 시작하세요."; return; }
        var selected = SelectedChoices().ToArray();
        if (selected.Length == 0 && !await _confirmation.ConfirmAsync(
            "빈 그룹 등록", "선택한 리소스가 없습니다. 빈 그룹으로 등록하시겠습니까?", "빈 그룹 등록", cancellationToken)) return;
        IsSaving = true;
        try
        {
            var resources = selected.Select(x => new GroupResource(x.Kind, x.Id, x.Name, x.Region)).ToArray();
            var order = StartOrder.Select(x => new GroupResourceRef(x.Kind, x.Id)).ToArray();
            _createdGroup = await _groups.CreateAsync(GroupName, _accountId, SelectedRegion.RegionCode,
                _profileName, resources, order, cancellationToken);
            _isComplete = true; _dirty = false;
            OnPropertyChanged(nameof(CreatedGroup)); OnPropertyChanged(nameof(IsComplete));
            OnPropertyChanged(nameof(IsStep1)); OnPropertyChanged(nameof(IsStep2));
            OnPropertyChanged(nameof(IsStep3)); OnPropertyChanged(nameof(IsStep4)); OnPropertyChanged(nameof(IsStep5));
            GroupCreated?.Invoke(this, EventArgs.Empty);
        }
        finally { IsSaving = false; }
    }

    private async Task QueryEc2Async(CancellationToken token)
    {
        if (SelectedRegion is null) return;
        var region = SelectedRegion.RegionCode;
        IsEc2Loading = true; Ec2Status = "조회 중…"; Message = "";
        try
        {
            var items = await _queries.ListEc2Async(region, token);
            var groups = await _groups.ListAsync(token);
            token.ThrowIfCancellationRequested();
            EnsureDraftConnectionCurrent();
            ReplaceChoices(Ec2Choices, items.Select(x => new ResourceChoice(AwsResourceKind.Ec2,
                x.InstanceId, string.IsNullOrWhiteSpace(x.Name) ? x.InstanceId : x.Name,
                x.InstanceType, x.State, region)), groups);
            _ec2HasResult = true;
            Ec2Status = items.Count == 0 ? "조회 완료 · 리소스 없음" : $"조회 완료 · {items.Count}개";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Ec2Status = FailureStatus(_ec2HasResult, ex); throw; }
        finally { IsEc2Loading = false; }
    }

    private async Task QueryRdsAsync(CancellationToken token)
    {
        if (SelectedRegion is null) return;
        var region = SelectedRegion.RegionCode;
        IsRdsLoading = true; RdsStatus = "조회 중…"; Message = "";
        try
        {
            var items = await _queries.ListRdsAsync(region, token);
            var groups = await _groups.ListAsync(token);
            token.ThrowIfCancellationRequested();
            EnsureDraftConnectionCurrent();
            ReplaceChoices(RdsChoices, items.Select(x => new ResourceChoice(AwsResourceKind.Rds,
                x.DbInstanceIdentifier, x.DbInstanceIdentifier, x.Engine, x.Status, region)), groups);
            _rdsHasResult = true;
            RdsStatus = items.Count == 0 ? "조회 완료 · 리소스 없음" : $"조회 완료 · {items.Count}개";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { RdsStatus = FailureStatus(_rdsHasResult, ex); throw; }
        finally { IsRdsLoading = false; }
    }

    private async Task QueryS3Async(CancellationToken token)
    {
        IsS3Loading = true; S3Status = "조회 중…"; Message = "";
        try
        {
            var items = await _queries.ListS3Async(token);
            var groups = await _groups.ListAsync(token);
            token.ThrowIfCancellationRequested();
            EnsureDraftConnectionCurrent();
            ReplaceChoices(S3Choices, items.Select(x => new ResourceChoice(AwsResourceKind.S3,
                x.Name, x.Name, x.Region, "—", x.Region)), groups);
            _s3HasResult = true;
            S3Status = items.Count == 0 ? "조회 완료 · 버킷 없음" : $"조회 완료 · {items.Count}개";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { S3Status = FailureStatus(_s3HasResult, ex); throw; }
        finally { IsS3Loading = false; }
    }

    private void ReplaceChoices(ObservableCollection<ResourceChoice> target,
        IEnumerable<ResourceChoice> incoming, IReadOnlyList<ServiceGroup> groups)
    {
        var selected = target.Where(x => x.IsSelected).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var old in target) old.PropertyChanged -= OnChoiceChanged;
        target.Clear();
        foreach (var choice in incoming)
        {
            choice.RegisteredGroup = groups.FirstOrDefault(group => group.AccountId == _accountId &&
                group.Resources.Any(resource => resource.Kind == choice.Kind &&
                    string.Equals(resource.Id, choice.Id, StringComparison.OrdinalIgnoreCase) &&
                    (choice.Kind == AwsResourceKind.S3 ||
                     string.Equals(group.Region, choice.Region, StringComparison.OrdinalIgnoreCase))))?.Name;
            choice.IsSelected = selected.Contains(choice.Id);
            choice.PropertyChanged += OnChoiceChanged;
            target.Add(choice);
        }
        SyncStartOrder(); NotifySelectionChanged();
    }

    private void ClearChoices(ObservableCollection<ResourceChoice> choices)
    {
        foreach (var choice in choices) choice.PropertyChanged -= OnChoiceChanged;
        choices.Clear(); SyncStartOrder(); NotifySelectionChanged();
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ResourceChoice.IsSelected)) return;
        _dirty = true; SyncStartOrder(); NotifySelectionChanged();
    }

    private IEnumerable<ResourceChoice> SelectedChoices() =>
        Ec2Choices.Concat(RdsChoices).Concat(S3Choices).Where(x => x.IsSelected);

    private void SyncStartOrder()
    {
        var selected = Ec2Choices.Concat(RdsChoices).Where(x => x.IsSelected).ToArray();
        for (var i = StartOrder.Count - 1; i >= 0; i--)
            if (!selected.Any(x => x.Kind == StartOrder[i].Kind && x.Id == StartOrder[i].Id)) StartOrder.RemoveAt(i);
        foreach (var choice in selected)
            if (!StartOrder.Any(x => x.Kind == choice.Kind && x.Id == choice.Id))
                StartOrder.Add(new StartOrderChoice(choice.Kind, choice.Id, choice.Name));
        NotifyOrderChanged();
    }

    private void MoveSelectedOrder(int delta)
    {
        if (SelectedOrder is null || IsLoading) return;
        var from = StartOrder.IndexOf(SelectedOrder);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= StartOrder.Count) return;
        StartOrder.Move(from, to); _dirty = true; NotifyOrderChanged();
    }

    private void NotifyOrderChanged()
    {
        OnPropertyChanged(nameof(StartOrderSummary)); OnPropertyChanged(nameof(CanMoveUp)); OnPropertyChanged(nameof(CanMoveDown));
    }
    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(SelectedCountText)); OnPropertyChanged(nameof(ReviewResources));
    }
    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(IsLoading)); OnPropertyChanged(nameof(CanSelectRegion));
        OnPropertyChanged(nameof(CanGoPrevious)); OnPropertyChanged(nameof(CanQuery));
        _next.RaiseCanExecuteChanged(); _save.RaiseCanExecuteChanged();
        _queryEc2.RaiseCanExecuteChanged(); _queryRds.RaiseCanExecuteChanged(); _queryS3.RaiseCanExecuteChanged();
    }
    private void NotifyConnectionChanged()
    {
        OnPropertyChanged(nameof(ActiveAccountText)); OnPropertyChanged(nameof(IsDraftConnectionCurrent)); RaiseCommands();
    }
    private void EnsureDraftConnectionCurrent()
    {
        if (!IsDraftConnectionCurrent)
            throw new AwsConnectionException(AwsConnectionFailureKind.Busy,
                "조회 중 AWS 연결이 변경되어 이전 연결의 결과는 표시하지 않았습니다.");
    }
    private void ShowError(Exception ex)
    {
        if (ex is AwsResourceQueryException { Kind: AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession })
            _connection.MarkReconnectRequired();
        Message = ex is AwsResourceQueryException or AwsConnectionException or AppSettingsException or ServiceGroupException
            ? ex.Message : "요청을 완료하지 못했습니다. 다시 시도하세요.";
    }
    private static string FailureStatus(bool previous, Exception ex) =>
        previous ? $"이전 목록 유지 · 조회 실패: {ex.Message}" : $"조회 실패: {ex.Message}";
}
