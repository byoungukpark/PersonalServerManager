using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

public sealed record MetricPoint(DateTimeOffset MeasuredAt, double Value);

public sealed record MetricSeries(string Key, string Title, string Unit, IReadOnlyList<MetricPoint> Points);

/// <summary>인증된 프로필 세션에서 CloudWatch 성능 지표를 읽는다.</summary>
public interface IAwsCloudWatchSession
{
    Task<IReadOnlyList<MetricSeries>> ReadMetricsAsync(
        GroupResource resource, TimeSpan range, CancellationToken cancellationToken);
}
