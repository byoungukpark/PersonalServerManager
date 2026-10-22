using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using PersonalServerManager.Commands;
using PersonalServerManager.Models;
using PersonalServerManager.Navigation;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Services.Dialogs;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Persistence;

namespace PersonalServerManager.ViewModels;

/// <summary>그룹 관리의 메모리 초안을 보관한다. 확인과 저장 전에는 원본 그룹을 바꾸지 않는다.</summary>
public sealed partial class GroupManagementViewModel : ViewModelBase
{
    private readonly ServiceGroupService _groups;
    private readonly AwsConnectionService _connection;
    private readonly IAwsResourceQuery _queries;
    private readonly IConfirmationService _confirmation;
    private readonly AsyncRelayCommand _saveCommand;
    private readonly AsyncRelayCommand _deleteCommand;
    private readonly AsyncRelayCommand _queryEc2Command;
    private readonly AsyncRelayCommand _queryRdsCommand;
    private readonly AsyncRelayCommand _queryS3Command;
    private readonly AsyncRelayCommand _backCommand;
    private ServiceGroup? _original;
    private string _name = "";
    private string _message = "";
    private string _ec2Status = "조회 전";
    private string _rdsStatus = "조회 전";
    private string _s3Status = "조회 전";
    private bool _isBusy;
    private int _loadVersion;
    private long? _queryConnectionVersion;
    private StartOrderChoice? _selectedOrder;
    private int _selectedResourceTabIndex;

    public GroupManagementViewModel(ServiceGroupService groups, AwsConnectionService connection,
        IAwsResourceQuery queries, IConfirmationService confirmation)
    {
        _groups = groups;
        _connection = connection;
        _queries = queries;
        _confirmation = confirmation;
        Ec2Choices = [];
        RdsChoices = [];
        S3Choices = [];
        StartOrder = [];
        InitializeHealth();
        _saveCommand = new AsyncRelayCommand(SaveAsync, () => HasChanges && !IsBusy && !IsControlRunning, ShowError);
        _deleteCommand = new AsyncRelayCommand(DeleteAsync, () => _original is not null && !IsBusy && !IsControlRunning, ShowError);
        _queryEc2Command = new AsyncRelayCommand(token => QueryResourcesAsync(AwsResourceKind.Ec2, token), CanQuery, ShowError);
        _queryRdsCommand = new AsyncRelayCommand(token => QueryResourcesAsync(AwsResourceKind.Rds, token), CanQuery, ShowError);
        _queryS3Command = new AsyncRelayCommand(token => QueryResourcesAsync(AwsResourceKind.S3, token), CanQuery, ShowError);
        MoveUpCommand = new RelayCommand(_ => MoveSelectedOrder(-1));
        MoveDownCommand = new RelayCommand(_ => MoveSelectedOrder(1));
        _backCommand = new AsyncRelayCommand(BackAsync, () => !IsBusy, ShowError);
        _connection.PropertyChanged += (_, _) => NotifyCommands();
    }

    public event Action<Guid>? ReturnToDetailRequested;
    public event Action? Deleted;
    public ObservableCollection<ResourceChoice> Ec2Choices { get; }
    public ObservableCollection<ResourceChoice> RdsChoices { get; }
    public ObservableCollection<ResourceChoice> S3Choices { get; }
    public ObservableCollection<StartOrderChoice> StartOrder { get; }
    public ICommand SaveCommand => _saveCommand;
    public ICommand DeleteCommand => _deleteCommand;
    public ICommand QueryEc2Command => _queryEc2Command;
    public ICommand QueryRdsCommand => _queryRdsCommand;
    public ICommand QueryS3Command => _queryS3Command;
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand BackCommand => _backCommand;
    public ServiceGroup? Original => _original;
    public string AccountId => _original?.AccountId ?? "—";
    public string Region => _original?.Region ?? "—";
    public string ProfileName => _original?.ProfileName ?? "—";
    public bool IsControlRunning => !_connection.CanChangeConnection;
    public bool CanDiscover => _original is { } group && _connection.IsConnected &&
        _connection.ActiveConnection?.AccountId == group.AccountId;
    public bool HasChanges => _original is { } original &&
        (!string.Equals(Name, original.Name, StringComparison.Ordinal) ||
         !SelectedResources().Select(ResourceKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
             .SetEquals(original.Resources.Select(ResourceKey)) ||
         !StartOrder.Select(item => new GroupResourceRef(item.Kind, item.Id))
             .SequenceEqual(original.StartOrder) || HealthChanged(original));
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IncludeEc2 => true;
    public bool IncludeRds => true;
    public bool IncludeS3 => true;
    // 관리 화면은 고정 하단 버튼 위의 남은 높이에 표를 맞춘다.
    public double ResourceTableMinHeight => 0;
    public int SelectedResourceTabIndex
    {
        get => _selectedResourceTabIndex;
        set => SetProperty(ref _selectedResourceTabIndex, value);
    }
    public string Name { get => _name; set { if (SetProperty(ref _name, value)) NotifyChanges(); } }
    public string Message { get => _message; private set { if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }
    public string Ec2Status { get => _ec2Status; private set => SetProperty(ref _ec2Status, value); }
    public string RdsStatus { get => _rdsStatus; private set => SetProperty(ref _rdsStatus, value); }
    public string S3Status { get => _s3Status; private set => SetProperty(ref _s3Status, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) NotifyCommands(); } }
    public StartOrderChoice? SelectedOrder
    {
        get => _selectedOrder;
        set
        {
            if (SetProperty(ref _selectedOrder, value))
            {
                OnPropertyChanged(nameof(CanMoveUp));
                OnPropertyChanged(nameof(CanMoveDown));
            }
        }
    }
    public bool CanMoveUp => SelectedOrder is { } item && StartOrder.IndexOf(item) > 0;
    public bool CanMoveDown => SelectedOrder is { } item && StartOrder.IndexOf(item) < StartOrder.Count - 1;

    /// <summary>AWS 호출 없이 저장된 구성을 초안으로 복사한다. 조회 실패 자원도 선택 상태로 유지한다.</summary>
    public async Task LoadAsync(Guid groupId, CancellationToken token = default)
    {
        var version = ++_loadVersion;
        // 다른 그룹을 여는 동안 이전 초안이 남아 편집되는 일을 막는다.
        _original = null;
        _queryConnectionVersion = null;
        Name = "";
        SetChoices(Ec2Choices, []);
        SetChoices(RdsChoices, []);
        SetChoices(S3Choices, []);
        StartOrder.Clear();
        ClearHealth();
        SelectedOrder = null;
        Message = "";
        OnPropertyChanged(nameof(AccountId));
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(ProfileName));
        NotifyChanges();
        NotifyCommands();
        IsBusy = true;
        try
        {
            var original = (await _groups.ListAsync(token)).FirstOrDefault(group => group.Id == groupId)
                ?? throw new ServiceGroupException("편집할 그룹을 찾을 수 없습니다.");
            if (version != _loadVersion) return;
            _original = original;
            _queryConnectionVersion = null;
            Name = original.Name;
            SetChoices(Ec2Choices, original.Resources.Where(item => item.Kind == AwsResourceKind.Ec2));
            SetChoices(RdsChoices, original.Resources.Where(item => item.Kind == AwsResourceKind.Rds));
            SetChoices(S3Choices, original.Resources.Where(item => item.Kind == AwsResourceKind.S3));
            StartOrder.Clear();
            foreach (var item in original.StartOrder)
            {
                var resource = original.Resources.Single(resource => resource.Kind == item.Kind && resource.Id == item.Id);
                StartOrder.Add(new StartOrderChoice(item.Kind, item.Id, resource.Name));
            }
            LoadHealth(original);
            Ec2Status = RdsStatus = S3Status = "조회 전 · 저장된 연결은 유지됩니다.";
            Message = "";
            OnPropertyChanged(nameof(AccountId));
            OnPropertyChanged(nameof(Region));
            OnPropertyChanged(nameof(ProfileName));
            NotifyChanges();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (version == _loadVersion) ShowError(exception);
        }
        finally { if (version == _loadVersion) IsBusy = false; }
    }

    public Task<bool> ConfirmLeaveAsync(CancellationToken token = default) =>
        !HasChanges ? Task.FromResult(true) : _confirmation.ConfirmAsync(
            "그룹 편집 중단", "저장하지 않은 변경사항이 있습니다. 이동하시겠습니까?", "이동", token);

    public void ReportNavigationError() => Message = "화면 이동 확인창을 열지 못했습니다. 다시 시도하세요.";

    /// <summary>마법사와 동일한 순서 항목을 드래그 또는 버튼으로 이동한다.</summary>
    public void MoveOrder(StartOrderChoice source, StartOrderChoice target)
    {
        var from = StartOrder.IndexOf(source);
        var to = StartOrder.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        StartOrder.Move(from, to);
        NotifyChanges();
    }

    public async Task QueryResourcesAsync(AwsResourceKind kind, CancellationToken token = default)
    {
        if (_original is not { } group || !CanDiscover) return;
        if (IsBusy || IsControlRunning) return;
        var active = _connection.ActiveConnection!;
        if (_queryConnectionVersion is { } previous && previous != active.ConnectionVersion)
            throw new ServiceGroupException("AWS 연결이 변경되었습니다. 그룹 관리를 다시 여세요.");
        var version = _loadVersion;
        IsBusy = true;
        Message = "";
        SetStatus(kind, "AWS 목록 조회 중…");
        try
        {
            // AWS 목록 API는 추가 후보를 찾기 위한 읽기 작업이며 등록 자원을 제어하지 않는다.
            var incoming = kind switch
            {
                AwsResourceKind.Ec2 => (await _queries.ListEc2Async(group.Region, token)).Select(item =>
                    new ResourceChoice(kind, item.InstanceId, string.IsNullOrWhiteSpace(item.Name) ? item.InstanceId : item.Name,
                        item.InstanceType, item.State, group.Region)),
                AwsResourceKind.Rds => (await _queries.ListRdsAsync(group.Region, token)).Select(item =>
                    new ResourceChoice(kind, item.DbInstanceIdentifier, item.DbInstanceIdentifier,
                        item.Engine, item.Status, group.Region)),
                _ => (await _queries.ListS3Async(token)).Select(item =>
                    new ResourceChoice(kind, item.Name, item.Name, item.Region, "—", item.Region))
            };
            var groups = await _groups.ListAsync(token);
            token.ThrowIfCancellationRequested();
            if (version != _loadVersion || !_connection.IsConnected ||
                _connection.ActiveConnection?.AccountId != group.AccountId ||
                _connection.ActiveConnection.ConnectionVersion != active.ConnectionVersion)
                throw new ServiceGroupException("조회 중 AWS 연결 또는 그룹이 변경되었습니다. 다시 열어 주세요.");
            _queryConnectionVersion = active.ConnectionVersion;
            MergeChoices(Choices(kind), incoming, groups, group);
            SetStatus(kind, $"조회 완료 · {Choices(kind).Count}개 (기존 연결은 유지)");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetStatus(kind, "조회 실패 · 기존 선택 유지");
            throw;
        }
        finally { IsBusy = false; }
    }

    private void MergeChoices(ObservableCollection<ResourceChoice> target, IEnumerable<ResourceChoice> incoming,
        IReadOnlyList<ServiceGroup> groups, ServiceGroup current)
    {
        var selected = target.Where(item => item.IsSelected).ToDictionary(ResourceKey, StringComparer.OrdinalIgnoreCase);
        var results = incoming.ToList();
        foreach (var existing in selected.Values.Where(item => results.All(candidate =>
            !string.Equals(ResourceKey(candidate), ResourceKey(item), StringComparison.OrdinalIgnoreCase))))
            results.Add(new ResourceChoice(existing.Kind, existing.Id, existing.Name, existing.Detail,
                "AWS 목록에서 찾을 수 없음 · 등록 유지", existing.Region));
        foreach (var old in target) old.PropertyChanged -= OnChoiceChanged;
        target.Clear();
        foreach (var choice in results)
        {
            var key = ResourceKey(choice);
            var row = selected.TryGetValue(key, out var existing)
                ? new ResourceChoice(choice.Kind, choice.Id, existing.Name, choice.Detail, choice.State, existing.Region)
                : choice;
            row.RegisteredGroup = groups.Where(group => group.Id != current.Id).FirstOrDefault(group =>
                group.AccountId == current.AccountId && group.Resources.Any(item => ResourceKey(item) == key))?.Name;
            row.IsSelected = existing is not null;
            row.PropertyChanged += OnChoiceChanged;
            target.Add(row);
        }
        SyncStartOrder();
        NotifyChanges();
    }

    private void SetChoices(ObservableCollection<ResourceChoice> target, IEnumerable<GroupResource> resources)
    {
        foreach (var old in target) old.PropertyChanged -= OnChoiceChanged;
        target.Clear();
        foreach (var resource in resources)
        {
            var choice = new ResourceChoice(resource.Kind, resource.Id, resource.Name,
                resource.Kind == AwsResourceKind.S3 ? resource.Region : "등록된 자원", "조회 전 · 등록 유지", resource.Region)
                { IsSelected = true };
            choice.PropertyChanged += OnChoiceChanged;
            target.Add(choice);
        }
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ResourceChoice.IsSelected)) return;
        SyncStartOrder();
        NotifyChanges();
    }

    private void SyncStartOrder()
    {
        var selected = Ec2Choices.Concat(RdsChoices).Where(item => item.IsSelected).ToArray();
        for (var index = StartOrder.Count - 1; index >= 0; index--)
            if (!selected.Any(item => item.Kind == StartOrder[index].Kind && item.Id == StartOrder[index].Id))
                StartOrder.RemoveAt(index);
        foreach (var item in selected)
            if (!StartOrder.Any(order => order.Kind == item.Kind && order.Id == item.Id))
                StartOrder.Add(new StartOrderChoice(item.Kind, item.Id, item.Name));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }

    private void MoveSelectedOrder(int offset)
    {
        if (SelectedOrder is not { } selected || IsBusy) return;
        var from = StartOrder.IndexOf(selected);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= StartOrder.Count) return;
        StartOrder.Move(from, to);
        NotifyChanges();
    }

    /// <summary>차이 확인 후에만 JSON을 갱신한다. 실패하거나 확인을 취소하면 초안을 유지한다.</summary>
    public async Task SaveAsync(CancellationToken token = default)
    {
        if (_original is not { } original || !HasChanges) return;
        var name = GroupRules.NormalizeName(Name);
        var resources = SelectedResources().ToArray();
        var order = StartOrder.Select(item => new GroupResourceRef(item.Kind, item.Id)).ToArray();
        var health = BuildHealthEndpoints(resources);
        var preview = DescribeChanges(original, name, resources, order) +
            (HealthChanged(original) ? $"\nHealth 앱: {(original.HealthEndpoints?.Count ?? 0)}개 → {health.Count}개" : "");
        if (!await _confirmation.ConfirmAsync("그룹 변경사항 저장", preview, "변경사항 저장", token)) return;
        IsBusy = true;
        try
        {
            var saved = await _groups.UpdateAsync(original, name, resources, order, _queryConnectionVersion, token, health);
            _original = saved;
            Name = saved.Name;
            NotifyChanges();
            Message = "그룹 변경사항을 저장했습니다. AWS 자원에는 제어 명령을 보내지 않았습니다.";
        }
        finally { IsBusy = false; }
    }

    private async Task DeleteAsync(CancellationToken token)
    {
        if (_original is not { } group) return;
        if (!await _confirmation.ConfirmAsync("그룹 삭제",
            $"'{group.Name}' 그룹을 삭제하시겠습니까?\n\n로컬 연결 정보만 삭제하며 AWS 자원은 중지하거나 삭제하지 않습니다.",
            "그룹 삭제", token)) return;
        IsBusy = true;
        try
        {
            await _groups.DeleteAsync(group.Id, token);
            _original = null;
            Deleted?.Invoke();
        }
        finally { IsBusy = false; }
    }

    private async Task BackAsync(CancellationToken token)
    {
        if (_original is not { } group || !await ConfirmLeaveAsync(token)) return;
        ReturnToDetailRequested?.Invoke(group.Id);
    }

    private IEnumerable<GroupResource> SelectedResources() =>
        Ec2Choices.Concat(RdsChoices).Concat(S3Choices).Where(item => item.IsSelected)
            .Select(item => new GroupResource(item.Kind, item.Id, item.Name, item.Region));

    private static string DescribeChanges(ServiceGroup original, string name,
        IReadOnlyList<GroupResource> resources, IReadOnlyList<GroupResourceRef> order)
    {
        var before = original.Resources.Select(ResourceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = resources.Select(ResourceKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = resources.Where(item => !before.Contains(ResourceKey(item)))
            .Select(item => $"{item.Kind} {item.Name} ({item.Id})").ToArray();
        var removed = original.Resources.Where(item => !after.Contains(ResourceKey(item)))
            .Select(item => $"{item.Kind} {item.Name} ({item.Id})").ToArray();
        var lines = new List<string>();
        if (original.Name != name) lines.Add($"이름: {original.Name} → {name}");
        if (added.Length > 0) lines.Add($"추가: {string.Join(", ", added)}");
        if (removed.Length > 0) lines.Add($"연결 해제: {string.Join(", ", removed)}");
        if (!original.StartOrder.SequenceEqual(order))
            lines.Add("시작 순서: " + (order.Count == 0 ? "대상 없음" :
                string.Join(" → ", order.Select(item => $"{item.Kind} {item.Id}"))));
        return string.Join("\n", lines) + "\n\n연결 해제는 로컬 설정만 바꾸며 AWS 자원을 중지하거나 삭제하지 않습니다.";
    }

    private bool CanQuery() => _original is not null && CanDiscover && !IsBusy && !IsControlRunning;
    private ObservableCollection<ResourceChoice> Choices(AwsResourceKind kind) => kind switch
    {
        AwsResourceKind.Ec2 => Ec2Choices,
        AwsResourceKind.Rds => RdsChoices,
        _ => S3Choices
    };
    private void SetStatus(AwsResourceKind kind, string status)
    {
        switch (kind)
        {
            case AwsResourceKind.Ec2: Ec2Status = status; break;
            case AwsResourceKind.Rds: RdsStatus = status; break;
            default: S3Status = status; break;
        }
    }
    private void NotifyChanges()
    {
        OnPropertyChanged(nameof(HasChanges));
        _saveCommand.RaiseCanExecuteChanged();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsControlRunning));
        OnPropertyChanged(nameof(CanDiscover));
        _saveCommand.RaiseCanExecuteChanged();
        _deleteCommand.RaiseCanExecuteChanged();
        _queryEc2Command.RaiseCanExecuteChanged();
        _queryRdsCommand.RaiseCanExecuteChanged();
        _queryS3Command.RaiseCanExecuteChanged();
        _backCommand.RaiseCanExecuteChanged();
    }
    private void ShowError(Exception exception)
    {
        if (exception is AwsResourceQueryException { Kind: AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession })
            _connection.MarkReconnectRequired();
        Message = exception is ServiceGroupException or AppSettingsException or AwsResourceQueryException
            ? exception.Message : "그룹 관리 작업을 완료하지 못했습니다. 다시 시도하세요.";
    }
    private static string ResourceKey(GroupResource resource) =>
        $"{resource.Kind}|{(resource.Kind == AwsResourceKind.S3 ? "" : resource.Region)}|{resource.Id}";
    private static string ResourceKey(ResourceChoice choice) =>
        $"{choice.Kind}|{(choice.Kind == AwsResourceKind.S3 ? "" : choice.Region)}|{choice.Id}";
}
