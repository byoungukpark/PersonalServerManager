using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using PersonalServerManager.Models;
using PersonalServerManager.Services.Aws;

namespace PersonalServerManager.ViewModels;

public sealed record MetricChartCard(string Title, string LatestText, string MeasuredAtText,
    string StatusText, bool HasData, Func<PlotModel>? PlotFactory,
    string? StatusBadge = null, bool IsFailed = false, bool IsMissing = false)
{
    public bool HasStatusBadge => StatusBadge is not null;
}

/// <summary>CloudWatch 측정값을 자원별 그래프 카드로 변환한다.</summary>
internal static class MetricChartFactory
{
    public static IReadOnlyList<MetricChartCard> Create(
        AwsResourceKind kind, IReadOnlyList<MetricSeries> series, int rangeHours)
    {
        var byKey = series.ToDictionary(item => item.Key, StringComparer.Ordinal);
        return kind switch
        {
            AwsResourceKind.Ec2 =>
            [
                CreateChart("CPU 사용률", [byKey["cpu"]], false, rangeHours),
                CreateChart("수신 트래픽 (구간 합계)", [byKey["networkIn"]], false, rangeHours),
                CreateChart("송신 트래픽 (구간 합계)", [byKey["networkOut"]], false, rangeHours),
                CreateStatusChart(byKey["statusCheckFailed"], rangeHours)
            ],
            AwsResourceKind.Rds =>
            [
                CreateChart("CPU 사용률", [byKey["cpu"]], false, rangeHours),
                CreateChart("DB 연결 수", [byKey["connections"]], false, rangeHours),
                CreateChart("가용 메모리", [byKey["freeableMemory"]], false, rangeHours),
                CreateChart("남은 저장 공간", [byKey["freeStorageSpace"]], false, rangeHours),
                CreateChart("읽기 지연", [byKey["readLatency"]], false, rangeHours),
                CreateChart("쓰기 지연", [byKey["writeLatency"]], false, rangeHours)
            ],
            AwsResourceKind.S3 =>
            [
                CreateChart("저장량", [byKey["storage"]], true, 96),
                CreateChart("객체 수", [byKey["objects"]], true, 96)
            ],
            _ => []
        };
    }

    private static MetricChartCard CreateStatusChart(MetricSeries series, int rangeHours)
    {
        var latest = series.Points.LastOrDefault();
        if (latest is null)
            return new MetricChartCard("상태 검사", "측정값 없음", "측정 시각 없음",
                "선택 기간에 상태 검사 측정값이 없습니다.", false, null, "데이터 없음", IsMissing: true);

        var failed = latest.Value >= 1;
        return new MetricChartCard("상태 검사", $"현재 {(failed ? 1 : 0)}",
            $"AWS 측정 시각 {latest.MeasuredAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            "0 정상 / 1 실패 · 과거 실패 구간 포함", true,
            () => CreatePlot([series], false, rangeHours, true),
            failed ? "실패" : "정상", IsFailed: failed);
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
        return new MetricChartCard(title, latestText,
            $"AWS 측정 시각 {measuredAt.ToLocalTime():yyyy-MM-dd HH:mm}", status, true,
            () => CreatePlot(series, daily, rangeHours));
    }

    private static string FormatValue(double value, string unit)
    {
        if (unit == "B" && value >= 1024 * 1024 * 1024) return $"{value / (1024 * 1024 * 1024):N1} GiB";
        if (unit == "B" && value >= 1024 * 1024) return $"{value / (1024 * 1024):N1} MiB";
        if (unit == "B" && value >= 1024) return $"{value / 1024:N1} KiB";
        return $"{value:N1} {unit}";
    }

    private static PlotModel CreatePlot(IReadOnlyList<MetricSeries> series, bool daily, int rangeHours,
        bool statusCheck = false)
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
            IsPanEnabled = false,
            IsZoomEnabled = false,
            AxislineColor = OxyColor.FromRgb(75, 91, 108),
            TextColor = model.TextColor
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            MinimumPadding = 0.1,
            AbsoluteMinimum = 0,
            AbsoluteMaximum = statusCheck ? 1 : double.MaxValue,
            Minimum = statusCheck ? 0 : double.NaN,
            Maximum = statusCheck ? 1 : double.NaN,
            MajorStep = statusCheck ? 1 : double.NaN,
            Unit = statusCheck ? string.Empty : series[0].Unit,
            IsPanEnabled = false,
            IsZoomEnabled = false,
            TextColor = model.TextColor
        });
        for (var index = 0; index < series.Count; index++)
        {
            var item = series[index];
            var line = statusCheck ? new StairStepSeries() : new LineSeries();
            line.Title = item.Title;
            line.Color = statusCheck ? OxyColor.FromRgb(246, 132, 120) :
                index == 0 ? OxyColor.FromRgb(94, 211, 207) : OxyColor.FromRgb(250, 185, 94);
            line.StrokeThickness = 2;
            line.MarkerType = MarkerType.Circle;
            line.MarkerSize = 2;
            line.TrackerFormatString = statusCheck
                ? "{0}\n{1}: {2:yyyy-MM-dd HH:mm}\n{3}: {4:0} (0 정상 / 1 실패)"
                : "{0}\n{1}: {2:yyyy-MM-dd HH:mm}\n{3}: {4:0.##} " + item.Unit;
            foreach (var point in item.Points)
                line.Points.Add(DateTimeAxis.CreateDataPoint(point.MeasuredAt.LocalDateTime, point.Value));
            model.Series.Add(line);
        }
        return model;
    }
}
