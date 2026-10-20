using System.Windows.Input;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Commands;
using PersonalServerManager.Navigation;

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

/// <summary>그룹에 저장된 구성만 표시하고 AWS 제어는 수행하지 않는다.</summary>
public sealed class GroupDetailViewModel : ViewModelBase
{
    private readonly ServiceGroupService _groups;
    private readonly GroupStatusService _status;
    private readonly AsyncRelayCommand _refreshCommand;
    private Guid? _groupId;
    private int _loadVersion;
    private ServiceGroup? _group;
    private IReadOnlyList<RegisteredResourceRow> _resources = [];
    private IReadOnlyList<StartOrderRow> _startOrder = [];
    private bool _isLoading;
    private string _errorMessage = string.Empty;
    private string _statusMessage = "AWS 상태 확인 전";
    private bool _hasAccessIssue;

    public GroupDetailViewModel(NavigationService navigation, ServiceGroupService groups, GroupStatusService status)
    {
        _groups = groups;
        _status = status;
        BackToDashboardCommand = new RelayCommand(_ => navigation.NavigateTo(AppPage.Dashboard));
        _refreshCommand = new AsyncRelayCommand(
            token => _groupId is { } id ? LoadAsync(id, token) : Task.CompletedTask,
            () => _groupId.HasValue && !IsLoading && !HasAccessIssue,
            ShowLoadError);
        GoToConnectionCommand = new RelayCommand(_ => navigation.NavigateTo(AppPage.AwsConnection));
    }

    public ICommand BackToDashboardCommand { get; }
    public ICommand GoToConnectionCommand { get; }
    public ICommand RefreshCommand => _refreshCommand;
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
            if (SetProperty(ref _hasAccessIssue, value)) _refreshCommand.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<RegisteredResourceRow> Resources
    {
        get => _resources;
        private set
        {
            if (SetProperty(ref _resources, value))
            {
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
