using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

/// <summary>조회 시작과 완료 때 연결 세대를 확인해 다른 프로필의 지표가 화면에 섞이지 않게 한다.</summary>
public sealed class AwsCloudWatchMetricQuery
{
    private readonly AwsConnectionService _connection;

    public AwsCloudWatchMetricQuery(AwsConnectionService connection)
    {
        _connection = connection;
    }

    public long? ConnectionVersion => _connection.ActiveConnection?.ConnectionVersion;

    public async Task<IReadOnlyList<MetricSeries>> ReadAsync(
        ServiceGroup group, GroupResource resource, TimeSpan range, CancellationToken cancellationToken)
    {
        var active = _connection.ActiveConnection;
        if (!_connection.IsConnected || active is null || active.AccountId != group.AccountId)
        {
            throw new AwsConnectionException(AwsConnectionFailureKind.Authentication,
                "그룹의 AWS 계정으로 연결한 뒤 지표를 조회하세요.");
        }

        if (!group.Resources.Contains(resource))
        {
            throw new ArgumentException("그룹에 등록되지 않은 리소스입니다.", nameof(resource));
        }

        var session = _connection.ActiveSession as IAwsCloudWatchSession;
        if (session is null)
        {
            throw new AwsConnectionException(AwsConnectionFailureKind.ProfileMissing,
                "CloudWatch 조회에 사용할 AWS 세션이 없습니다.");
        }

        var resourceInRegion = string.IsNullOrWhiteSpace(resource.Region)
            ? resource with { Region = group.Region }
            : resource;
        IReadOnlyList<MetricSeries> series;
        try
        {
            series = await session.ReadMetricsAsync(resourceInRegion, range, cancellationToken);
        }
        catch (AwsResourceQueryException exception) when (exception.Kind is
            AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession)
        {
            _connection.MarkReconnectRequired();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connection.IsConnected || _connection.ActiveConnection?.ConnectionVersion != active.ConnectionVersion ||
            !ReferenceEquals(session, _connection.ActiveSession))
        {
            throw new AwsConnectionException(AwsConnectionFailureKind.Busy,
                "조회 중 AWS 연결이 변경되어 이전 지표를 표시하지 않았습니다.");
        }

        return series;
    }
}
