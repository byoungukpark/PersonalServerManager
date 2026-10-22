using System.Collections.ObjectModel;
using System.Windows.Input;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Groups;
using PersonalServerManager.Services.Persistence;
using PersonalServerManager.Commands;
using PersonalServerManager.Navigation;

namespace PersonalServerManager.ViewModels;

public sealed class CardServiceStatus : ViewModelBase
{
    private string _summary;
    private string _detail = string.Empty;
    private string _tone = "unknown";
    private DateTimeOffset? _lastSuccess;
    private string? _lastSummary;

    public CardServiceStatus(AwsResourceKind kind)
    {
        Kind = kind;
        _summary = $"○ {KindLabel} 상태 확인 전";
    }

    public AwsResourceKind Kind { get; }
    public string KindLabel => Kind.ToString().ToUpperInvariant();
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public string Tone { get => _tone; private set => SetProperty(ref _tone, value); }
    public DateTimeOffset? LastSuccess => _lastSuccess;

    public void Reset(string message)
    {
        _lastSuccess = null;
        _lastSummary = null;
        Summary = $"○ {KindLabel} {message}";
        Detail = message;
        Tone = "unknown";
    }

    public void Apply(ServiceReadResult result, ServiceGroup group)
    {
        if (result.Error is { } error)
        {
            Summary = _lastSuccess is null
                ? $"⚠ {KindLabel} 상태 확인 불가"
                : $"⚠ {_lastSummary} · 이전 상태 · 조회 실패";
            Detail = _lastSuccess is null ? error : $"{error}\n마지막 정상 조회: {LocalTime(_lastSuccess.Value)}";
            Tone = "error";
            return;
        }

        var registered = group.Resources.Where(resource => resource.Kind == Kind).ToArray();
        var states = registered.Select(resource => result.States.TryGetValue(resource.Id, out var state)
            ? state : "Unknown").ToArray();
        var running = 0;
        var stopped = 0;
        if (Kind == AwsResourceKind.S3)
        {
            var available = states.Count(state => state == "조회 가능");
            Summary = $"● S3 조회 가능 {available}/{registered.Length}";
        }
        else
        {
            running = states.Count(state => Kind == AwsResourceKind.Ec2
                ? state.Equals("running", StringComparison.OrdinalIgnoreCase)
                : state.Equals("available", StringComparison.OrdinalIgnoreCase));
            stopped = states.Count(state => state.Equals("stopped", StringComparison.OrdinalIgnoreCase));
            var other = states.Length - running - stopped;
            Summary = $"● {KindLabel} 실행 {running} · 중지 {stopped}" +
                (other > 0 ? $" · 기타/확인 불가 {other}" : string.Empty);
        }

        _lastSuccess = result.ReadAt;
        _lastSummary = Summary.TrimStart('●', ' ');
        Detail = $"마지막 정상 조회: {LocalTime(result.ReadAt ?? DateTimeOffset.Now)}";
        Tone = states.Any(state => state is "Unknown" or "버킷 없음") ? "unknown" :
            Kind == AwsResourceKind.S3 || running == states.Length ? "success" :
            stopped == states.Length ? "stopped" :
            running + stopped < states.Length ? "transitioning" : "mixed";
    }

    private static string LocalTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class ServiceGroupCard : ViewModelBase
{
    private ServiceGroup _group;
    private bool _isRefreshing;
    private string _accessMessage = string.Empty;

    public ServiceGroupCard(ServiceGroup group)
    {
        _group = group;
        Statuses = new ObservableCollection<CardServiceStatus>(
            Enum.GetValues<AwsResourceKind>()
                .Where(kind => group.Resources.Any(resource => resource.Kind == kind))
                .Select(kind => new CardServiceStatus(kind)));
    }

    public ServiceGroup Group => _group;
    public Guid Id => Group.Id;
    public string Name => Group.Name;
    public string AccountId => Group.AccountId;
    public string Region => Group.Region;
    public int Ec2Count => Group.Resources.Count(resource => resource.Kind == AwsResourceKind.Ec2);
    public int RdsCount => Group.Resources.Count(resource => resource.Kind == AwsResourceKind.Rds);
    public int S3Count => Group.Resources.Count(resource => resource.Kind == AwsResourceKind.S3);
    public ObservableCollection<CardServiceStatus> Statuses { get; }
    public string AccessMessage
    {
        get => _accessMessage;
        private set
        {
            if (SetProperty(ref _accessMessage, value)) OnPropertyChanged(nameof(CanRefresh));
        }
    }
    public bool CanRefresh => string.IsNullOrEmpty(AccessMessage) && !IsRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (SetProperty(ref _isRefreshing, value)) OnPropertyChanged(nameof(CanRefresh));
        }
    }
    public string LastSuccessText
    {
        get
        {
            var latest = Statuses.Where(status => status.LastSuccess.HasValue)
                .Select(status => status.LastSuccess!.Value).DefaultIfEmpty().Max();
            return latest == default ? "마지막 정상 조회: 없음" :
                $"마지막 정상 조회: {latest.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
    }

    public void UpdateGroup(ServiceGroup group)
    {
        _group = group;
        OnPropertyChanged(nameof(Group));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(AccountId));
        OnPropertyChanged(nameof(Region));
        OnPropertyChanged(nameof(Ec2Count));
        OnPropertyChanged(nameof(RdsCount));
        OnPropertyChanged(nameof(S3Count));
        foreach (var kind in Enum.GetValues<AwsResourceKind>())
        {
            var hasResource = group.Resources.Any(resource => resource.Kind == kind);
            var status = Statuses.FirstOrDefault(item => item.Kind == kind);
            if (hasResource && status is null) Statuses.Add(new CardServiceStatus(kind));
            if (!hasResource && status is not null) Statuses.Remove(status);
        }
    }

    public void Apply(GroupReadResult result)
    {
        AccessMessage = result.AccessMessage ?? string.Empty;
        if (result.AccessMessage is not null)
        {
            foreach (var status in Statuses) status.Reset("조회 대기");
        }
        else
        {
            foreach (var service in result.Services)
                Statuses.FirstOrDefault(status => status.Kind == service.Kind)?.Apply(service, Group);
        }
        OnPropertyChanged(nameof(LastSuccessText));
    }
}

/// <summary>저장 그룹과 등록 자원의 기본 AWS 상태를 카드에 표시한다.</summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly ServiceGroupService _groups;
    private readonly GroupStatusService _status;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly AsyncRelayCommand _refreshCommand;
    private bool _isLoading;
    private int _groupVersion;
    private string _errorMessage = string.Empty;

    public DashboardViewModel(NavigationService navigation, ServiceGroupService groups, GroupStatusService status)
    {
        _groups = groups;
        _status = status;
        _groups.GroupDeleted += RemoveGroup;
        CreateGroupCommand = new RelayCommand(_ => navigation.NavigateTo(AppPage.GroupWorkspace));
        OpenGroupCommand = new RelayCommand(OpenGroup);
        RefreshGroupCommand = new RelayCommand(RefreshGroup);
        _refreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading, ShowLoadError);
    }

    public event Action<Guid>? OpenGroupRequested;
    public event Action<Guid, GroupReadResult>? GroupStatusRead;
    public event Action<Guid, bool>? GroupStatusRefreshChanged;
    public ObservableCollection<ServiceGroupCard> Groups { get; } = [];
    public ICommand CreateGroupCommand { get; }
    public ICommand OpenGroupCommand { get; }
    public ICommand RefreshGroupCommand { get; }
    public ICommand RefreshCommand => _refreshCommand;

    private void OpenGroup(object? parameter)
    {
        if (parameter is ServiceGroupCard card) OpenGroupRequested?.Invoke(card.Id);
    }

    private async void RefreshGroup(object? parameter)
    {
        if (parameter is not ServiceGroupCard card) return;
        try { await RefreshGroupAsync(card); }
        catch (Exception exception) { ShowLoadError(exception); }
    }
    public bool HasGroups => Groups.Count > 0;
    public bool IsEmpty => !IsLoading && !HasGroups && !HasError;
    public string GroupCountText => $"{Groups.Count}개";
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
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
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>저장된 그룹을 다시 읽고 현재 연결 계정의 기본 상태를 갱신한다.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;
            var version = _groupVersion;
            var groups = await _groups.ListAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (version != _groupVersion)
            {
                return;
            }
            var previous = Groups.ToDictionary(card => card.Id);
            Groups.Clear();
            foreach (var group in groups.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                var card = previous.TryGetValue(group.Id, out var existing) ? existing : new ServiceGroupCard(group);
                card.UpdateGroup(group);
                Groups.Add(card);
            }
            OnPropertyChanged(nameof(HasGroups));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(GroupCountText));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ShowLoadError(exception);
            return;
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
        }

        await RefreshStatusesAsync(cancellationToken);
    }

    public async Task RefreshStatusesAsync(CancellationToken cancellationToken = default)
    {
        if (!await _statusGate.WaitAsync(0, cancellationToken)) return;
        try
        {
            foreach (var card in Groups.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UpdateCardAsync(card, cancellationToken);
            }
        }
        finally
        {
            _statusGate.Release();
        }
    }

    public async Task RefreshGroupAsync(ServiceGroupCard card, CancellationToken cancellationToken = default)
    {
        if (!await _statusGate.WaitAsync(0, cancellationToken)) return;
        try { await UpdateCardAsync(card, cancellationToken); }
        finally { _statusGate.Release(); }
    }

    private async Task UpdateCardAsync(ServiceGroupCard card, CancellationToken cancellationToken)
    {
        if (!Groups.Contains(card)) return;
        card.IsRefreshing = true;
        GroupStatusRefreshChanged?.Invoke(card.Id, true);
        try
        {
            var result = await _status.ReadAsync(card.Group, cancellationToken);
            if (Groups.Contains(card))
            {
                card.Apply(result);
                GroupStatusRead?.Invoke(card.Id, result);
            }
        }
        finally
        {
            card.IsRefreshing = false;
            GroupStatusRefreshChanged?.Invoke(card.Id, false);
        }
    }

    /// <summary>저장 성공 후 카드와 그 카드의 상태를 제거하고 이전 목록 응답을 무효화한다.</summary>
    private void RemoveGroup(Guid groupId)
    {
        _groupVersion++;
        var card = Groups.FirstOrDefault(item => item.Id == groupId);
        if (card is not null) Groups.Remove(card);
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(GroupCountText));
    }

    private void ShowLoadError(Exception exception)
    {
        ErrorMessage = exception is AppSettingsException
            ? exception.Message
            : "저장된 서비스 그룹을 불러오지 못했습니다. 다시 시도해 주세요.";
    }
}
