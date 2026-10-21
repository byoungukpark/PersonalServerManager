using System.Windows.Input;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Commands;
using PersonalServerManager.Navigation;
using PersonalServerManager.Services.Dialogs;
using PersonalServerManager.Services.Aws;

namespace PersonalServerManager.ViewModels;

public sealed record RegisteredResourceRow(string Kind, string Name, string Id, string Region, string Status)
{
    public string Tone => Status.Contains("이전 상태", StringComparison.Ordinal) ? "error" :
        Status is "running" or "available" or "조회 가능" ? "success" :
        Status == "stopped" ? "stopped" :
        Status is "starting" or "stopping" or "rebooting" or "modifying" ? "transitioning" : "unknown";
    public string DisplayStatus => $"{(Tone == "error" ? "⚠" : Tone == "unknown" ? "○" : "●")} {Status}";
}
public sealed record StartOrderRow(int Position, string Kind, string Name, string Id);

/// <summary>등록된 그룹 구성·상태와 그룹/개별 자원 제어를 제공한다.</summary>
public sealed class GroupDetailViewModel : ViewModelBase
{
    private readonly ServiceGroupService _groups;
    private readonly GroupStatusService _status;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _deleteCommand;
    private readonly NavigationService _navigation;
    private readonly IConfirmationService _confirmation;
    private bool _isDeleting;
    private readonly ResourceControlService? _control;
    private readonly Dictionary<ResourceControlAction, AsyncRelayCommand> _controlCommands;
    private readonly Dictionary<ResourceControlAction, AsyncRelayCommand> _groupControlCommands;
    private RegisteredResourceRow? _selectedResource;
    private int _selectedServiceIndex;
    private string _resourceSearch = string.Empty;
    private bool _isConfirmingControl;
    private Guid? _groupId;
    private int _loadVersion;
    private ServiceGroup? _group;
    private IReadOnlyList<RegisteredResourceRow> _resources = [];
    private IReadOnlyList<StartOrderRow> _startOrder = [];
    private bool _isLoading;
    private string _errorMessage = string.Empty;
    private string _statusMessage = "AWS 상태 확인 전";
    private bool _hasAccessIssue;

    public GroupDetailViewModel(NavigationService navigation, ServiceGroupService groups, GroupStatusService status,
        IConfirmationService confirmation, ResourceControlService? control = null)
    {
        _navigation = navigation;
        _confirmation = confirmation;
        _groups = groups;
        _status = status;
        _control = control;
        BackToDashboardCommand = new RelayCommand(_ => navigation.NavigateTo(AppPage.Dashboard));
        _refreshCommand = new AsyncRelayCommand(
            token => _groupId is { } id ? LoadAsync(id, token) : Task.CompletedTask,
            () => _groupId.HasValue && !IsLoading && !HasAccessIssue && !IsDeleting,
            ShowLoadError);
        _deleteCommand = new AsyncRelayCommand(DeleteAsync, () => HasGroup && !IsDeleting && !IsControlRunning && !_isConfirmingControl,
            exception => ErrorMessage = exception is AppSettingsException
                ? exception.Message : "서비스 그룹을 삭제하지 못했습니다. 다시 시도해 주세요.");
        GoToConnectionCommand = new RelayCommand(_ => navigation.NavigateTo(AppPage.AwsConnection));
        _controlCommands = Enum.GetValues<ResourceControlAction>().ToDictionary(action => action, action =>
            new AsyncRelayCommand(token => ControlSelectedAsync(action, token), () => CanRun(action),
                exception => ErrorMessage = exception is ServiceGroupException
                    ? exception.Message : "제어 작업을 시작하지 못했습니다."));
        _groupControlCommands = new[] { ResourceControlAction.Start, ResourceControlAction.Stop }
            .ToDictionary(action => action, action => new AsyncRelayCommand(
                token => ControlGroupAsync(action, token), CanRunGroup,
                exception => ErrorMessage = exception is ServiceGroupException
                    ? exception.Message : "그룹 제어 작업을 시작하지 못했습니다."));
        if (_control is not null)
        {
            _control.Activity.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(IsControlRunning));
                RefreshControlCommands();
                _deleteCommand.RaiseCanExecuteChanged();
            };
        }
    }

    public ICommand BackToDashboardCommand { get; }
    public ICommand GoToConnectionCommand { get; }
    public ICommand RefreshCommand => _refreshCommand;
    public ICommand DeleteCommand => _deleteCommand;
    public ICommand StartCommand => _controlCommands[ResourceControlAction.Start];
    public ICommand StopCommand => _controlCommands[ResourceControlAction.Stop];
    public ICommand RebootCommand => _controlCommands[ResourceControlAction.Reboot];
    public ICommand StartGroupCommand => _groupControlCommands[ResourceControlAction.Start];
    public ICommand StopGroupCommand => _groupControlCommands[ResourceControlAction.Stop];
    public bool IsControlRunning => _control?.Activity.IsControlRunning == true;
    public bool IsControlSelection => SelectedResource is { Kind: "EC2" or "RDS" };
    public string SelectionMessage => SelectedResource is null ? "리소스를 선택하세요." :
        SelectedResource.Kind == "S3" ? "S3는 조회만 제공하며 시작·중지 대상이 아닙니다." :
        $"{SelectedResource.Name} · {SelectedResource.Id} · {SelectedResource.Status}";
    public RegisteredResourceRow? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (!SetProperty(ref _selectedResource, value)) return;
            OnPropertyChanged(nameof(SelectionMessage));
            OnPropertyChanged(nameof(IsControlSelection));
            RefreshControlCommands();
        }
    }
    public int SelectedServiceIndex
    {
        get => _selectedServiceIndex;
        set
        {
            if (!SetProperty(ref _selectedServiceIndex, value)) return;
            SelectedResource = null;
            OnPropertyChanged(nameof(FilteredResources));
        }
    }
    public string ResourceSearch
    {
        get => _resourceSearch;
        set
        {
            if (!SetProperty(ref _resourceSearch, value)) return;
            SelectedResource = null;
            OnPropertyChanged(nameof(FilteredResources));
        }
    }
    public IReadOnlyList<RegisteredResourceRow> FilteredResources => Resources.Where(row =>
        row.Kind == (SelectedServiceIndex == 0 ? "EC2" : SelectedServiceIndex == 1 ? "RDS" : "S3") &&
        (row.Name.Contains(ResourceSearch, StringComparison.OrdinalIgnoreCase) ||
         row.Id.Contains(ResourceSearch, StringComparison.OrdinalIgnoreCase))).ToArray();
    public bool IsDeleting
    {
        get => _isDeleting;
        private set
        {
            if (SetProperty(ref _isDeleting, value))
            {
                _deleteCommand.RaiseCanExecuteChanged();
                _refreshCommand.RaiseCanExecuteChanged();
                RefreshControlCommands();
            }
        }
    }
    public string Name => _group?.Name ?? "서비스 그룹";
    public string AccountId => _group?.AccountId ?? "—";
    public string Region => _group?.Region ?? "—";
    public string ProfileName => _group?.ProfileName ?? "—";
    public string Ec2CountText => $"EC2 {_group?.Resources.Count(resource => resource.Kind == AwsResourceKind.Ec2) ?? 0}개";
    public string RdsCountText => $"RDS {_group?.Resources.Count(resource => resource.Kind == AwsResourceKind.Rds) ?? 0}개";
    public string S3CountText => $"S3 {_group?.Resources.Count(resource => resource.Kind == AwsResourceKind.S3) ?? 0}개";
    public bool HasGroup => _group is not null;
    public bool HasResources => Resources.Count > 0;
    public bool HasStartOrder => StartOrder.Count > 0;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public Guid? CurrentGroupId => _groupId;
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasAccessIssue
    {
        get => _hasAccessIssue;
        private set
        {
            if (SetProperty(ref _hasAccessIssue, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
                RefreshControlCommands();
            }
        }
    }

    public IReadOnlyList<RegisteredResourceRow> Resources
    {
        get => _resources;
        private set
        {
            if (SetProperty(ref _resources, value))
            {
                var selected = SelectedResource;
                OnPropertyChanged(nameof(FilteredResources));
                SelectedResource = selected is null ? null : value.FirstOrDefault(row => row.Kind == selected.Kind && row.Id == selected.Id);
                OnPropertyChanged(nameof(HasResources));
            }
        }
    }

    public IReadOnlyList<StartOrderRow> StartOrder
    {
        get => _startOrder;
        private set
        {
            if (SetProperty(ref _startOrder, value))
            {
                OnPropertyChanged(nameof(HasStartOrder));
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                _refreshCommand.RaiseCanExecuteChanged();
                RefreshControlCommands();
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>선택한 그룹을 저장소에서 다시 읽어 최신 로컬 구성을 표시한다.</summary>
    public async Task LoadAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (IsDeleting) return;
        var sameGroup = _groupId == groupId;
        var previous = sameGroup ? Resources.ToDictionary(row => (row.Kind, row.Id)) : [];
        _groupId = groupId;
        var version = ++_loadVersion;
        if (!sameGroup)
        {
            SetGroup(null);
            Resources = [];
            StartOrder = [];
            StatusMessage = "AWS 상태 확인 전";
            HasAccessIssue = false;
        }
        ErrorMessage = string.Empty;
        IsLoading = true;
        try
        {
            var groups = await _groups.ListAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != _loadVersion)
            {
                return;
            }

            var group = groups.FirstOrDefault(item => item.Id == groupId);
            if (group is null)
            {
                ErrorMessage = "저장된 서비스 그룹을 찾을 수 없습니다. 대시보드에서 목록을 다시 확인해 주세요.";
                return;
            }

            SetGroup(group);
            Resources = group.Resources
                .OrderBy(resource => resource.Kind)
                .ThenBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
                .Select(resource => new RegisteredResourceRow(
                    KindLabel(resource.Kind),
                    resource.Name,
                    resource.Id,
                    string.IsNullOrWhiteSpace(resource.Region) ? group.Region : resource.Region,
                    previous.TryGetValue((KindLabel(resource.Kind), resource.Id), out var old)
                        ? old.Status : "상태 확인 전"))
                .ToArray();

            StartOrder = group.StartOrder.Select((item, index) =>
            {
                var resource = group.Resources.FirstOrDefault(resource =>
                    resource.Kind == item.Kind &&
                    string.Equals(resource.Id, item.Id, StringComparison.Ordinal));
                return new StartOrderRow(index + 1, KindLabel(item.Kind), resource?.Name ?? item.Id, item.Id);
            }).ToArray();

            await RefreshStatusAsync(group, version, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (version == _loadVersion)
            {
                ShowLoadError(exception);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    private async Task RefreshStatusAsync(ServiceGroup group, int version, CancellationToken cancellationToken)
    {
        var result = await _status.ReadAsync(group, cancellationToken);
        if (version != _loadVersion) return;

        if (result.AccessMessage is { } access)
        {
            HasAccessIssue = true;
            StatusMessage = access;
            Resources = Resources.Select(row => row with { Status = "조회 대기" }).ToArray();
            return;
        }

        HasAccessIssue = false;
        var messages = new List<string>();
        var rows = Resources.ToArray();
        foreach (var service in result.Services)
        {
            var label = KindLabel(service.Kind);
            if (service.Error is { } error)
            {
                messages.Add($"{label} 조회 실패: {error}");
                rows = rows.Select(row => row.Kind == label
                    ? row with { Status = row.Status is "상태 확인 전" or "상태 확인 불가"
                        ? "상태 확인 불가"
                        : $"{row.Status.Replace(" · 이전 상태", "")} · 이전 상태" }
                    : row).ToArray();
                continue;
            }

            rows = rows.Select(row => row.Kind == label
                ? row with { Status = service.States.TryGetValue(row.Id, out var state) ? state : "Unknown" }
                : row).ToArray();
            messages.Add($"{label} 정상 조회 {service.ReadAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        }
        Resources = rows;
        StatusMessage = messages.Count == 0 ? "등록된 자원이 없습니다." : string.Join(" · ", messages);
    }

    private void SetGroup(ServiceGroup? group)
    {
        _group = group;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccountId));
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(Ec2CountText));
        OnPropertyChanged(nameof(RdsCountText));
        OnPropertyChanged(nameof(S3CountText));
        OnPropertyChanged(nameof(HasGroup));
        _deleteCommand.RaiseCanExecuteChanged();
        RefreshControlCommands();
    }

    /// <summary>확정한 로컬 그룹만 삭제하며 저장 성공 전에는 화면 구성을 유지한다.</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (_group is null || IsDeleting || IsControlRunning || _isConfirmingControl) return;
        var group = _group;
        IsDeleting = true;
        ErrorMessage = string.Empty;
        try
        {
            var confirmed = await _confirmation.ConfirmAsync("그룹 삭제",
                $"'{group.Name}' 그룹을 삭제하시겠습니까?\n\n로컬 그룹 정보만 삭제하며 AWS 자원은 중지하거나 삭제하지 않습니다.",
                "그룹 삭제", cancellationToken);
            if (!confirmed) return;

            if (IsControlRunning) throw new ServiceGroupException("제어 작업 중에는 그룹을 삭제할 수 없습니다.");

            await _groups.DeleteAsync(group.Id, cancellationToken);
            // 삭제 전에 시작한 상태 조회가 나중에 도착해도 화면에 반영하지 않는다.
            _loadVersion++;
            _groupId = null;
            SetGroup(null);
            Resources = [];
            StartOrder = [];
            IsLoading = false;
            if (_navigation.CurrentPage == AppPage.GroupDetail)
            {
                _navigation.NavigateTo(AppPage.Dashboard);
            }
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private bool CanRun(ResourceControlAction action)
    {
        if (_control is null || !_control.CanControl(_group) || IsDeleting || IsLoading ||
            _isConfirmingControl || SelectedResource is null || HasAccessIssue) return false;
        if (SelectedResource.Kind == "S3") return false;
        if (action == ResourceControlAction.Reboot)
            return SelectedResource.Kind == "EC2" && SelectedResource.Status == "running";
        return action == ResourceControlAction.Start ? SelectedResource.Status == "stopped" :
            SelectedResource.Status == (SelectedResource.Kind == "EC2" ? "running" : "available");
    }

    private bool CanRunGroup() => _control?.CanControl(_group) == true &&
        _group?.StartOrder.Count > 0 && !IsDeleting && !IsLoading && !_isConfirmingControl && !HasAccessIssue;

    private void RefreshControlCommands()
    {
        if (_controlCommands is null) return;
        foreach (var command in _controlCommands.Values) command.RaiseCanExecuteChanged();
        if (_groupControlCommands is not null)
            foreach (var command in _groupControlCommands.Values) command.RaiseCanExecuteChanged();
    }

    /// <summary>확인창의 실행 순서와 연결 세대를 고정하고 실행 서비스에서 한 번 더 검증한다.</summary>
    public async Task ControlGroupAsync(ResourceControlAction action, CancellationToken cancellationToken = default)
    {
        if (!CanRunGroup() || _group is null || _control is null) return;
        var group = _group;
        var plan = ResourceControlService.BuildGroupPlan(group, action);
        var version = _control.ConnectionVersion!.Value;
        _isConfirmingControl = true;
        RefreshControlCommands();
        _deleteCommand.RaiseCanExecuteChanged();
        try
        {
            if (!await _confirmation.ConfirmAsync("AWS 그룹 제어 확인",
                _control.GroupConfirmationText(group, action, plan), $"그룹 {ResourceControlService.ActionLabel(action)}",
                cancellationToken)) return;
            await _control.ExecuteGroupAsync(group.Id, action, version, plan);
            if (_groupId == group.Id) await LoadAsync(group.Id);
        }
        finally
        {
            _isConfirmingControl = false;
            RefreshControlCommands();
            _deleteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>확인 대상을 고정하고 화면 이동 후에도 서비스의 제어 처리를 계속한다.</summary>
    public async Task ControlSelectedAsync(ResourceControlAction action, CancellationToken cancellationToken = default)
    {
        if (!CanRun(action) || _group is null || SelectedResource is null || _control is null) return;
        var group = _group;
        var row = SelectedResource;
        var kind = row.Kind == "EC2" ? AwsResourceKind.Ec2 : AwsResourceKind.Rds;
        var resource = group.Resources.Single(item => item.Kind == kind && item.Id == row.Id);
        var version = _control.ConnectionVersion!.Value;
        _isConfirmingControl = true;
        RefreshControlCommands();
        _deleteCommand.RaiseCanExecuteChanged();
        try
        {
            if (!await _confirmation.ConfirmAsync("AWS 개별 제어 확인",
                _control.ConfirmationText(group, resource, action), ResourceControlService.ActionLabel(action), cancellationToken)) return;
            await _control.ExecuteAsync(group.Id, kind, row.Id, action, version);
            if (_groupId == group.Id) await LoadAsync(group.Id);
        }
        finally
        {
            _isConfirmingControl = false;
            RefreshControlCommands();
            _deleteCommand.RaiseCanExecuteChanged();
        }
    }

    private void ShowLoadError(Exception exception)
    {
        ErrorMessage = exception is AppSettingsException
            ? exception.Message
            : "서비스 그룹 구성을 불러오지 못했습니다. 다시 시도해 주세요.";
    }

    private static string KindLabel(AwsResourceKind kind) => kind switch
    {
        AwsResourceKind.Ec2 => "EC2",
        AwsResourceKind.Rds => "RDS",
        AwsResourceKind.S3 => "S3",
        _ => kind.ToString()
    };
}
