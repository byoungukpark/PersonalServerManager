using System.ComponentModel;
using System.Windows.Input;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using PersonalServerManager.Models;
using PersonalServerManager.Navigation;
using PersonalServerManager.Services.Aws;
using PersonalServerManager.Commands;

namespace PersonalServerManager.ViewModels;

public sealed record MetricRangeOption(int Hours, string Label);

public sealed record MetricChartCard(string Title, string LatestText, string MeasuredAtText,
    string StatusText, bool HasData, PlotModel? Plot);

public sealed partial class GroupDetailViewModel
{
    private static readonly IReadOnlyList<MetricRangeOption> AvailableMetricRanges =
        [new(1, "1시간"), new(3, "3시간"), new(12, "12시간"), new(24, "24시간")];
    private readonly AwsCloudWatchMetricQuery? _metrics;
    private readonly AsyncRelayCommand _metricRefreshCommand;
    private CancellationTokenSource? _metricCancellation;
    private int _metricVersion;
    private int _selectedDetailTabIndex;
    private bool _isMetricsLoading;
    private string _metricStatusMessage = "모니터링 탭에서 지표를 조회합니다.";
    private string _metricErrorMessage = string.Empty;
    private IReadOnlyList<MetricChartCard> _metricCharts = [];
    private DateTimeOffset? _lastMetricFetch;
    private (Guid GroupId, string ResourceId, long ConnectionVersion)? _cachedS3Key;
    private IReadOnlyList<MetricSeries>? _cachedS3Series;
    private DateTimeOffset _cachedS3At;
    private MetricRangeOption _selectedMetricRange = AvailableMetricRanges[0];

    public IReadOnlyList<MetricRangeOption> MetricRanges => AvailableMetricRanges;

    public ICommand RefreshMetricsCommand => _metricRefreshCommand;
    public IReadOnlyList<MetricChartCard> MetricCharts
    {
        get => _metricCharts;
        private set => SetProperty(ref _metricCharts, value);
    }
    public int SelectedDetailTabIndex
    {
        get => _selectedDetailTabIndex;
        set
        {
            if (!SetProperty(ref _selectedDetailTabIndex, value)) return;
            OnPropertyChanged(nameof(ResourcePanelHeight));
            _metricRefreshCommand.RaiseCanExecuteChanged();
            if (value == 1) OnMetricSelectionChanged();
            else CancelMetricRefresh();
        }
    }
    public double ResourcePanelHeight => SelectedDetailTabIndex == 1 ? 730 : 500;
    public MetricRangeOption SelectedMetricRange
    {
        get => _selectedMetricRange;
        set
        {
            if (value is null || !SetProperty(ref _selectedMetricRange, value)) return;
            if (SelectedDetailTabIndex == 1 && SelectedResource?.Kind != "S3") OnMetricSelectionChanged();
        }
    }
    public bool IsTimeRangeEnabled => SelectedResource?.Kind != "S3";
    public bool IsMetricsLoading
    {
        get => _isMetricsLoading;
        private set
        {
            if (SetProperty(ref _isMetricsLoading, value)) _metricRefreshCommand.RaiseCanExecuteChanged();
        }
    }
    public string MetricStatusMessage
    {
        get => _metricStatusMessage;
        private set => SetProperty(ref _metricStatusMessage, value);
    }
    public string MetricErrorMessage
    {
        get => _metricErrorMessage;
        private set
        {
            if (SetProperty(ref _metricErrorMessage, value)) OnPropertyChanged(nameof(HasMetricError));
        }
    }
    public bool HasMetricError => !string.IsNullOrWhiteSpace(MetricErrorMessage);

    /// <summary>상세 화면의 선택이 바뀌면 이전 요청을 취소하고 새 대상만 조회한다.</summary>
    private void OnMetricSelectionChanged()
    {
        CancelMetricRefresh();
        ClearMetrics();
        OnPropertyChanged(nameof(IsTimeRangeEnabled));
        _metricRefreshCommand.RaiseCanExecuteChanged();
        if (!CanRefreshMetrics()) return;

        if (TryShowCachedS3Metrics()) return;
        _ = RefreshMetricsAsync(CancellationToken.None);
    }

    private bool TryShowCachedS3Metrics()
    {
        if (SelectedResource?.Kind != "S3" || _groupId is not { } groupId ||
            _cachedS3Key != (groupId, SelectedResource.Id, _metrics?.ConnectionVersion ?? 0) ||
            _cachedS3Series is null || DateTimeOffset.UtcNow - _cachedS3At >= TimeSpan.FromHours(1)) return false;

        ApplyMetrics(_cachedS3Series, true);
        _lastMetricFetch = _cachedS3At;
        return true;
    }

    private bool CanRefreshMetrics()
    {
        return _metrics is not null && _group is not null && SelectedResource is not null &&
            SelectedDetailTabIndex == 1 && _navigation.CurrentPage == AppPage.GroupDetail &&
            !HasAccessIssue && !IsDeleting && !IsMetricsLoading;
    }

    /// <summary>30초 기본 상태 타이머에서 호출하지만 상세 지표는 5분(일 단위 S3는 1시간) 간격만 허용한다.</summary>
    public async Task RefreshMetricsIfDueAsync()
    {
        if (!CanRefreshMetrics()) return;
        var interval = SelectedResource?.Kind == "S3" ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(5);
        if (_lastMetricFetch is { } last && DateTimeOffset.UtcNow - last < interval) return;
        await RefreshMetricsAsync(CancellationToken.None);
    }

    /// <summary>조회 실패 시 이전 그래프를 유지하고 오류를 분리해 표시한다.</summary>
    public async Task RefreshMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefreshMetrics() || _group is null || SelectedResource is null || _metrics is null) return;
        var group = _group;
        var selected = SelectedResource;
        var resource = group.Resources.FirstOrDefault(item => item.Id == selected.Id &&
            item.Kind.ToString().Equals(selected.Kind, StringComparison.OrdinalIgnoreCase));
        if (resource is null) return;

        CancelMetricRefresh();
        var version = ++_metricVersion;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _metricCancellation = source;
        IsMetricsLoading = true;
        MetricErrorMessage = string.Empty;
        MetricStatusMessage = "CloudWatch 지표를 조회하는 중입니다…";
        try
        {
            var range = TimeSpan.FromHours(SelectedMetricRange.Hours);
            var result = await _metrics.ReadAsync(group, resource, range, source.Token);
            if (version != _metricVersion || !IsCurrentMetricTarget(group.Id, selected)) return;

            ApplyMetrics(result, false);
            _lastMetricFetch = DateTimeOffset.UtcNow;
            if (resource.Kind == AwsResourceKind.S3)
            {
                _cachedS3Key = (group.Id, resource.Id, _metrics.ConnectionVersion ?? 0);
                _cachedS3Series = result;
                _cachedS3At = _lastMetricFetch.Value;
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // 화면 이탈·선택 변경으로 취소한 요청은 오류로 표시하지 않는다.
        }
        catch (AwsResourceQueryException exception) when (version == _metricVersion)
        {
            ShowMetricError(exception);
        }
        catch (Exception exception) when (version == _metricVersion)
        {
            ShowMetricError(exception);
        }
        finally
        {
            if (version == _metricVersion)
            {
                IsMetricsLoading = false;
                if (ReferenceEquals(_metricCancellation, source)) _metricCancellation = null;
            }
            source.Dispose();
        }
    }

    private bool IsCurrentMetricTarget(Guid groupId, RegisteredResourceRow selected)
    {
        return _groupId == groupId && _navigation.CurrentPage == AppPage.GroupDetail &&
            SelectedDetailTabIndex == 1 && SelectedResource?.Kind == selected.Kind &&
            SelectedResource.Id == selected.Id;
    }

    private void ApplyMetrics(IReadOnlyList<MetricSeries> series, bool cached)
    {
        var byKey = series.ToDictionary(item => item.Key, StringComparer.Ordinal);
        MetricCharts = SelectedResource?.Kind switch
        {
            "EC2" => CreateEc2Charts(byKey),
            "RDS" => CreateRdsCharts(byKey),
            "S3" => CreateS3Charts(byKey),
            _ => []
        };
        MetricErrorMessage = string.Empty;
        var sourceText = cached ? "저장된 일 단위 조회 결과" : "CloudWatch 조회 완료";
        var shownAt = cached ? _cachedS3At : DateTimeOffset.Now;
        MetricStatusMessage = $"{sourceText} · {shownAt:yyyy-MM-dd HH:mm:ss}";
    }

    private IReadOnlyList<MetricChartCard> CreateEc2Charts(IReadOnlyDictionary<string, MetricSeries> series)
    {
        return [CreateChart("CPU 사용률", [series["cpu"]], false, SelectedMetricRange.Hours),
            CreateChart("네트워크 송수신 (구간 합계)",
                [series["networkIn"], series["networkOut"]], false, SelectedMetricRange.Hours)];
    }

    private IReadOnlyList<MetricChartCard> CreateRdsCharts(IReadOnlyDictionary<string, MetricSeries> series)
    {
        return [CreateChart("CPU 사용률", [series["cpu"]], false, SelectedMetricRange.Hours),
            CreateChart("DB 연결 수", [series["connections"]], false, SelectedMetricRange.Hours)];
    }

    private static IReadOnlyList<MetricChartCard> CreateS3Charts(IReadOnlyDictionary<string, MetricSeries> series)
    {
        return [CreateChart("저장량", [series["storage"]], true, 96),
            CreateChart("객체 수", [series["objects"]], true, 96)];
    }

    private static MetricChartCard CreateChart(string title, IReadOnlyList<MetricSeries> series,
        bool daily, int rangeHours)
    {
        var latestPoints = series.Select(item => (Series: item, Point: item.Points.LastOrDefault()))
            .Where(item => item.Point is not null).ToArray();
        if (latestPoints.Length == 0)
            return new MetricChartCard(title, "데이터 없음", "측정 시각 없음",
                daily ? "S3 일 단위 지표가 아직 수집되지 않았습니다." : "선택 기간에 측정값이 없습니다.", false, null);

        var latestText = string.Join("  ·  ", latestPoints.Select(item =>
            $"{item.Series.Title} {FormatValue(item.Point!.Value, item.Series.Unit)}"));
        var measuredAt = latestPoints.Max(item => item.Point!.MeasuredAt);
        var status = daily && DateTimeOffset.UtcNow - measuredAt > TimeSpan.FromDays(2)
            ? "일 단위 지표 수집이 지연됐을 수 있습니다." : string.Empty;
        var plot = CreatePlot(series, daily, rangeHours);
        return new MetricChartCard(title, latestText,
            $"AWS 측정 시각 {measuredAt.ToLocalTime():yyyy-MM-dd HH:mm}", status, true, plot);
    }

    private static string FormatValue(double value, string unit)
    {
        if (unit == "B" && value >= 1024 * 1024 * 1024) return $"{value / (1024 * 1024 * 1024):N1} GiB";
        if (unit == "B" && value >= 1024 * 1024) return $"{value / (1024 * 1024):N1} MiB";
        if (unit == "B" && value >= 1024) return $"{value / 1024:N1} KiB";
        return $"{value:N1} {unit}";
    }

    private static PlotModel CreatePlot(IReadOnlyList<MetricSeries> series, bool daily, int rangeHours)
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            PlotAreaBorderColor = OxyColor.FromRgb(75, 91, 108),
            TextColor = OxyColor.FromRgb(219, 229, 239),
            DefaultFont = "Segoe UI"
        };
        model.Axes.Add(new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = daily ? "MM-dd" : "HH:mm",
            Minimum = DateTimeAxis.ToDouble(DateTime.Now.AddHours(-rangeHours)),
            Maximum = DateTimeAxis.ToDouble(DateTime.Now),
            AxislineColor = OxyColor.FromRgb(75, 91, 108),
            TextColor = model.TextColor
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            MinimumPadding = 0.1,
            AbsoluteMinimum = 0,
            Unit = series[0].Unit,
            TextColor = model.TextColor
        });

        for (var index = 0; index < series.Count; index++)
        {
            var item = series[index];
            var line = new LineSeries
            {
                Title = item.Title,
                Color = index == 0 ? OxyColor.FromRgb(94, 211, 207) : OxyColor.FromRgb(250, 185, 94),
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 2,
                TrackerFormatString = "{0}\n{1}: {2:yyyy-MM-dd HH:mm}\n{3}: {4:0.##} " + item.Unit
            };
            foreach (var point in item.Points)
                line.Points.Add(DateTimeAxis.CreateDataPoint(point.MeasuredAt.LocalDateTime, point.Value));
            model.Series.Add(line);
        }

        return model;
    }

    private void ShowMetricError(Exception exception)
    {
        MetricErrorMessage = exception is AwsResourceQueryException or AwsConnectionException
            ? exception.Message : "CloudWatch 지표 조회에 실패했습니다. 잠시 후 다시 시도하세요.";
        if (exception is AwsResourceQueryException { Kind: AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession })
        {
            MetricStatusMessage = "AWS 재연결 후 다시 조회하세요.";
            return;
        }
        MetricStatusMessage = _lastMetricFetch is { } last
            ? $"이전 측정값 표시 중 · 마지막 정상 조회 {last.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "지표 조회 실패";
    }

    private void ClearMetrics()
    {
        MetricCharts = [];
        MetricErrorMessage = string.Empty;
        MetricStatusMessage = SelectedResource is null ? "리소스를 선택하세요." : "모니터링 탭에서 지표를 조회합니다.";
        _lastMetricFetch = null;
    }

    public void CancelMetricRefresh()
    {
        _metricVersion++;
        _metricCancellation?.Cancel();
        _metricCancellation = null;
        IsMetricsLoading = false;
    }

    public void ResetMetricsForConnection()
    {
        CancelMetricRefresh();
        _cachedS3Key = null;
        _cachedS3Series = null;
        ClearMetrics();
        if (CanRefreshMetrics()) _ = RefreshMetricsAsync(CancellationToken.None);
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NavigationService.CurrentPage) &&
            _navigation.CurrentPage != AppPage.GroupDetail)
        {
            CancelMetricRefresh();
            IsStatusRefreshing = false;
        }
    }
}
