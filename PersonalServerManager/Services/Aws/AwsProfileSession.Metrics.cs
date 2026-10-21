using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.Runtime;
using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

internal sealed partial class AwsProfileSession
{
    /// <summary>선택 리소스의 지표만 한 번의 CloudWatch 데이터 요청으로 읽는다.</summary>
    public async Task<IReadOnlyList<MetricSeries>> ReadMetricsAsync(
        GroupResource resource, TimeSpan range, CancellationToken cancellationToken)
    {
        try
        {
            var region = string.IsNullOrWhiteSpace(resource.Region) ? _defaultRegion.SystemName : resource.Region;
            using var client = new AmazonCloudWatchClient(_credentials, RegionEndpoint.GetBySystemName(region));
            var end = DateTime.UtcNow;
            if (resource.Kind == AwsResourceKind.S3)
            {
                return await ReadDailyS3MetricsAsync(client, resource.Id, end, cancellationToken);
            }

            var period = range <= TimeSpan.FromHours(3) ? 60 : 300;
            var definitions = CreateComputeDefinitions(resource, period);
            var data = await GetMetricDataAsync(client, definitions, end - range, end, cancellationToken);
            return definitions.Select(definition => new MetricSeries(
                definition.Key, definition.Title, definition.Unit, data[definition.Key])).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonServiceException exception)
        {
            throw MapMetricError(exception);
        }
        catch (AmazonClientException exception)
        {
            var mapped = MapQueryClientError(exception);
            throw new AwsResourceQueryException(mapped.Kind, "CloudWatch에 연결하지 못했습니다. 네트워크를 확인하세요.");
        }
    }

    private static List<MetricDefinition> CreateComputeDefinitions(GroupResource resource, int period)
    {
        var isEc2 = resource.Kind == AwsResourceKind.Ec2;
        var metricNamespace = isEc2 ? "AWS/EC2" : "AWS/RDS";
        var dimensionName = isEc2 ? "InstanceId" : "DBInstanceIdentifier";
        var definitions = new List<MetricDefinition>
        {
            new("cpu", "CPU 사용률", "%", metricNamespace, "CPUUtilization", "Average", period,
                [new Dimension { Name = dimensionName, Value = resource.Id }])
        };

        if (isEc2)
        {
            definitions.Add(new MetricDefinition("networkIn", "네트워크 수신", "B", metricNamespace,
                "NetworkIn", "Sum", period, [new Dimension { Name = dimensionName, Value = resource.Id }]));
            definitions.Add(new MetricDefinition("networkOut", "네트워크 송신", "B", metricNamespace,
                "NetworkOut", "Sum", period, [new Dimension { Name = dimensionName, Value = resource.Id }]));
        }
        else
        {
            definitions.Add(new MetricDefinition("connections", "DB 연결 수", "개", metricNamespace,
                "DatabaseConnections", "Average", period,
                [new Dimension { Name = dimensionName, Value = resource.Id }]));
        }

        return definitions;
    }

    /// <summary>S3 저장량은 StorageType별로 발행되므로 목록을 찾은 뒤 같은 날짜의 값을 합산한다.</summary>
    private static async Task<IReadOnlyList<MetricSeries>> ReadDailyS3MetricsAsync(
        AmazonCloudWatchClient client, string bucketName, DateTime end, CancellationToken cancellationToken)
    {
        var storageTypes = new HashSet<string>(StringComparer.Ordinal);
        string? nextToken = null;
        do
        {
            // ListMetrics는 버킷에 실제로 발행된 저장 클래스별 지표를 찾는 읽기 API다.
            var response = await client.ListMetricsAsync(new ListMetricsRequest
            {
                Namespace = "AWS/S3",
                MetricName = "BucketSizeBytes",
                Dimensions = [new DimensionFilter { Name = "BucketName", Value = bucketName }],
                NextToken = nextToken
            }, cancellationToken);
            foreach (var metric in response.Metrics ?? [])
            {
                var storageType = metric.Dimensions?.FirstOrDefault(dimension => dimension.Name == "StorageType")?.Value;
                if (!string.IsNullOrWhiteSpace(storageType)) storageTypes.Add(storageType);
            }
            nextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        var definitions = new List<MetricDefinition>();
        var index = 0;
        foreach (var storageType in storageTypes.OrderBy(value => value, StringComparer.Ordinal))
        {
            definitions.Add(new MetricDefinition($"size{index++}", "저장량", "B", "AWS/S3",
                "BucketSizeBytes", "Average", 86400,
                [new Dimension { Name = "BucketName", Value = bucketName },
                 new Dimension { Name = "StorageType", Value = storageType }]));
        }
        definitions.Add(new MetricDefinition("objects", "객체 수", "개", "AWS/S3",
            "NumberOfObjects", "Average", 86400,
            [new Dimension { Name = "BucketName", Value = bucketName },
             new Dimension { Name = "StorageType", Value = "AllStorageTypes" }]));

        // S3 저장 지표는 하루에 한 번 발행되므로 화면의 시간 범위와 무관하게 최근 4일을 조회한다.
        var data = await GetMetricDataAsync(client, definitions, end.AddDays(-4), end, cancellationToken);
        var storagePoints = CombineStorageTypes(definitions, data);

        return [new MetricSeries("storage", "저장량 (보고된 StorageType 합계)", "B", storagePoints),
            new MetricSeries("objects", "객체 수", "개", data["objects"])];
    }

    private static IReadOnlyList<MetricPoint> CombineStorageTypes(
        IReadOnlyList<MetricDefinition> definitions,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> data)
    {
        var pointsByDay = new SortedDictionary<DateTime, (DateTimeOffset Latest, double Bytes)>();
        foreach (var definition in definitions)
        {
            if (!definition.Key.StartsWith("size", StringComparison.Ordinal)) continue;
            foreach (var point in data[definition.Key])
            {
                var day = point.MeasuredAt.UtcDateTime.Date;
                if (pointsByDay.TryGetValue(day, out var total))
                    pointsByDay[day] = (point.MeasuredAt > total.Latest ? point.MeasuredAt : total.Latest,
                        total.Bytes + point.Value);
                else
                    pointsByDay[day] = (point.MeasuredAt, point.Value);
            }
        }

        return pointsByDay.Values.Select(total => new MetricPoint(total.Latest, total.Bytes)).ToArray();
    }

    private static async Task<Dictionary<string, IReadOnlyList<MetricPoint>>> GetMetricDataAsync(
        AmazonCloudWatchClient client, IReadOnlyList<MetricDefinition> definitions,
        DateTime start, DateTime end, CancellationToken cancellationToken)
    {
        var values = definitions.ToDictionary(definition => definition.Key,
            _ => new SortedDictionary<DateTimeOffset, double>(), StringComparer.Ordinal);
        string? nextToken = null;
        do
        {
            var request = new GetMetricDataRequest
            {
                StartTime = start,
                EndTime = end,
                NextToken = nextToken,
                MetricDataQueries = definitions.Select(CreateMetricDataQuery).ToList()
            };
            // GetMetricData는 여러 지표를 한 요청으로 읽으며 응답의 NextToken까지 모두 소비한다.
            var response = await client.GetMetricDataAsync(request, cancellationToken);
            foreach (var result in response.MetricDataResults ?? [])
            {
                if (result.StatusCode?.Value is "Forbidden")
                    throw new AwsResourceQueryException(AwsQueryFailureKind.AccessDenied,
                        "CloudWatch 지표 조회 권한이 없습니다.");
                if (result.StatusCode?.Value is "InternalError" or "PartialData")
                    throw new AwsResourceQueryException(AwsQueryFailureKind.Other,
                        "CloudWatch가 지표 일부를 전달하지 못했습니다. 잠시 후 다시 조회하세요.");
                if (!values.TryGetValue(result.Id, out var points)) continue;
                var timestamps = result.Timestamps ?? [];
                var measurements = result.Values ?? [];
                for (var index = 0; index < Math.Min(timestamps.Count, measurements.Count); index++)
                {
                    var measuredAt = new DateTimeOffset(DateTime.SpecifyKind(timestamps[index], DateTimeKind.Utc));
                    points[measuredAt] = measurements[index];
                }
            }
            nextToken = response.NextToken;
        } while (!string.IsNullOrEmpty(nextToken));

        return values.ToDictionary(entry => entry.Key,
            entry => (IReadOnlyList<MetricPoint>)entry.Value
                .Select(point => new MetricPoint(point.Key, point.Value)).ToArray(), StringComparer.Ordinal);
    }

    private static MetricDataQuery CreateMetricDataQuery(MetricDefinition definition)
    {
        return new MetricDataQuery
        {
            Id = definition.Key,
            ReturnData = true,
            MetricStat = new MetricStat
            {
                Metric = new Amazon.CloudWatch.Model.Metric
                {
                    Namespace = definition.Namespace,
                    MetricName = definition.MetricName,
                    Dimensions = definition.Dimensions
                },
                Period = definition.Period,
                Stat = definition.Statistic
            }
        };
    }

    private static AwsResourceQueryException MapMetricError(AmazonServiceException exception)
    {
        var mapped = MapQueryError(exception);
        var message = mapped.Kind switch
        {
            AwsQueryFailureKind.AccessDenied => "CloudWatch 지표 조회 권한이 없습니다.",
            AwsQueryFailureKind.ExpiredSession or AwsQueryFailureKind.Authentication => mapped.Message,
            _ => "CloudWatch 지표를 불러오지 못했습니다. 잠시 후 다시 시도하세요."
        };
        return new AwsResourceQueryException(mapped.Kind, message);
    }

    private sealed record MetricDefinition(string Key, string Title, string Unit, string Namespace,
        string MetricName, string Statistic, int Period, List<Dimension> Dimensions);
}
