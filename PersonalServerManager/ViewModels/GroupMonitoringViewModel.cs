using System.Windows.Input;
using PersonalServerManager.Commands;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Aws;

namespace PersonalServerManager.ViewModels;

public sealed record MetricRangeOption(int Hours, string Label);
public sealed record MetricLayoutOption(int Columns, string Label);

/// <summary>한 등록 자원의 지표와 오류를 다른 자원과 독립적으로 관리한다.</summary>
public sealed class ResourceMetricCardViewModel : ViewModelBase
{
    private IReadOnlyList<MetricChartCard> _charts = [];
    private string _statusMessage = "조회 전";
    private string _errorMessage = string.Empty;
    private bool _isLoading;
    private DateTimeOffset? _lastFetch;
    private int _lastRangeHours;

    public ResourceMetricCardViewModel(GroupResource resource)
    {
        Resource = resource;
    }

    public GroupResource Resource { get; }
    public string Heading => $"{Resource.Kind.ToString().ToUpperInvariant()} · {Resource.Name}";
    public string Identity => Resource.Id;
    public bool IsS3 => Resource.Kind == AwsResourceKind.S3;
    public IReadOnlyList<MetricChartCard> Charts
    {
        get => _charts;
        private set => SetProperty(ref _charts, value);
    }
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsDue(int rangeHours, DateTimeOffset now)
    {
        var interval = IsS3 ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);
        return _lastFetch is null || (!IsS3 && _lastRangeHours != rangeHours) ||
            now - _lastFetch.Value >= interval;
    }

    public void Clear()
    {
        Charts = [];
        StatusMessage = "조회 전";
        ErrorMessage = string.Empty;
        _lastFetch = null;
    }

    public async Task RefreshAsync(ServiceGroup group, AwsCloudWatchMetricQuery metrics,
        int rangeHours, CancellationToken cancellationToken)
    {
        if (IsS3 && !IsDue(rangeHours, DateTimeOffset.UtcNow)) return;
        IsLoading = true;
        ErrorMessage = string.Empty;
        var previousStatus = StatusMessage;
        StatusMessage = "CloudWatch 조회 중…";
        try
        {
            var series = await metrics.ReadAsync(group, Resource, TimeSpan.FromHours(rangeHours), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Charts = MetricChartFactory.Create(Resource.Kind, series, rangeHours);
            _lastFetch = DateTimeOffset.UtcNow;
            _lastRangeHours = rangeHours;
            StatusMessage = $"CloudWatch 조회 완료 · {_lastFetch.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 화면 이탈이나 연결 변경으로 취소한 결과는 카드에 반영하지 않는다.
            StatusMessage = previousStatus;
        }
        catch (Exception exception)
        {
            ErrorMessage = exception is AwsResourceQueryException or AwsConnectionException
                ? exception.Message : "CloudWatch 지표 조회에 실패했습니다. 다시 시도하세요.";
            StatusMessage = _lastFetch is { } last
                ? $"이전 측정값 표시 중 · 마지막 정상 조회 {last.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : "지표 조회 실패";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

/// <summary>그룹의 모든 등록 자원 카드와 조회 주기를 관리한다.</summary>
public sealed class GroupMonitoringViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<MetricRangeOption> Ranges =
        [new(1, "1시간"), new(3, "3시간"), new(12, "12시간"), new(24, "24시간")];
    private static readonly IReadOnlyList<MetricLayoutOption> Layouts =
        [new(2, "그리드"), new(1, "스택")];
    private readonly AwsCloudWatchMetricQuery? _metrics;
    private readonly AsyncRelayCommand _refreshCommand;
    private IReadOnlyList<ResourceMetricCardViewModel> _cards = [];
    private MetricRangeOption _selectedRange = Ranges[0];
    private MetricLayoutOption _selectedLayout = Layouts[0];
    private ServiceGroup? _group;
    private CancellationTokenSource? _refreshCancellation;
    private bool _isActive;
    private bool _hasAccessIssue;
    private bool _isRefreshing;
    private string _errorMessage = string.Empty;
    private int _version;

    public GroupMonitoringViewModel(AwsCloudWatchMetricQuery? metrics)
    {
        _metrics = metrics;
        _refreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh,
            _ => ErrorMessage = "그룹 지표 조회를 시작하지 못했습니다.");
    }

    public IReadOnlyList<MetricRangeOption> MetricRanges => Ranges;
    public IReadOnlyList<MetricLayoutOption> MetricLayouts => Layouts;
    public MetricLayoutOption SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (value is null || !SetProperty(ref _selectedLayout, value)) return;
            // 배치 변경은 조회 결과를 다시 요청하지 않고 카드만 재배치한다.
            OnPropertyChanged(nameof(ChartColumns));
        }
    }
    public int ChartColumns => SelectedLayout.Columns;
    public ICommand RefreshCommand => _refreshCommand;
    public IReadOnlyList<ResourceMetricCardViewModel> Cards
    {
        get => _cards;
        private set
        {
            if (SetProperty(ref _cards, value)) OnPropertyChanged(nameof(HasResources));
        }
    }
    public bool HasResources => Cards.Count > 0;
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }
    public MetricRangeOption SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (value is null || !SetProperty(ref _selectedRange, value)) return;
            if (_isActive)
            {
                Cancel();
                _ = RefreshAsync();
            }
        }
    }
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value)) _refreshCommand.RaiseCanExecuteChanged();
        }
    }

    public void SetGroup(ServiceGroup? group)
    {
        var sameGroup = _group?.Id == group?.Id && _group?.UpdatedAt == group?.UpdatedAt;
        _group = group;
        if (sameGroup) return;
        Cancel();
        ErrorMessage = string.Empty;
        Cards = group?.Resources.Select(resource => new ResourceMetricCardViewModel(resource)).ToArray() ?? [];
        _refreshCommand.RaiseCanExecuteChanged();
        if (_isActive) _ = RefreshAsync();
    }

    public void SetAccessIssue(bool value)
    {
        _hasAccessIssue = value;
        if (value) Cancel();
        _refreshCommand.RaiseCanExecuteChanged();
    }

    public void SetActive(bool value)
    {
        if (_isActive == value) return;
        _isActive = value;
        if (value) _ = RefreshIfDueAsync();
        else Cancel();
        _refreshCommand.RaiseCanExecuteChanged();
    }

    public void ResetForConnection()
    {
        Cancel();
        foreach (var card in Cards) card.Clear();
        if (_isActive) _ = RefreshAsync();
    }

    public void Cancel()
    {
        _version++;
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        IsRefreshing = false;
    }

    private bool CanRefresh() => _isActive && !_hasAccessIssue && _group is not null &&
        Cards.Count > 0 && _metrics is not null && !IsRefreshing;

    public Task RefreshIfDueAsync() => RefreshCoreAsync(false, CancellationToken.None);
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(true, cancellationToken);

    private async Task RefreshCoreAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!CanRefresh() || _group is null || _metrics is null) return;
        var due = Cards.Where(card => manual && !card.IsS3 ||
            card.IsDue(SelectedRange.Hours, DateTimeOffset.UtcNow)).ToArray();
        if (due.Length == 0) return;
        var group = _group;
        var rangeHours = SelectedRange.Hours;
        var version = ++_version;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _refreshCancellation = source;
        IsRefreshing = true;
        try
        {
            // 동시에 과도한 CloudWatch 요청이 나가지 않도록 그룹당 최대 세 건만 실행한다.
            using var throttle = new SemaphoreSlim(3);
            await Task.WhenAll(due.Select(async card =>
            {
                await throttle.WaitAsync(source.Token);
                try
                {
                    await card.RefreshAsync(group, _metrics, rangeHours, source.Token);
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (version == _version) ErrorMessage = "그룹 지표 조회 중 오류가 발생했습니다.";
        }
        finally
        {
            if (version == _version)
            {
                _refreshCancellation = null;
                IsRefreshing = false;
            }
            source.Dispose();
        }
    }
}
