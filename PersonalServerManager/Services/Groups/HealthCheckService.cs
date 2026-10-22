using System.Net.Http;
using System.Text.Json;
using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Groups;

public enum HealthCheckState { Healthy, Unhealthy, ConnectionFailed, Unknown }

public sealed record HealthCheckResult(Guid EndpointId, DateTimeOffset CheckedAt,
    HealthCheckState State, string Message, int? StatusCode = null)
{
    public string Display => State switch
    {
        HealthCheckState.Healthy => "✓ 정상",
        HealthCheckState.Unhealthy => "⚠ 비정상",
        HealthCheckState.ConnectionFailed => "⚠ 연결 실패",
        _ => "○ 확인 불가"
    };
    public string DisplayLine => $"{CheckedAt.ToLocalTime():MM-dd HH:mm:ss} · {Display} · {Message}";
}

/// <summary>인증 없는 HTTP 점검을 실행하고 앱 수명 동안 최근 결과 10개만 보관한다.</summary>
public sealed class HealthCheckService : IDisposable
{
    private readonly HttpClient _client;
    private readonly TimeProvider _clock;
    private readonly Dictionary<Guid, Task<HealthCheckResult>> _inFlight = [];
    private readonly Dictionary<Guid, List<HealthCheckResult>> _history = [];
    private readonly Dictionary<Guid, HealthEndpoint> _definitions = [];
    private readonly object _gate = new();
    private readonly bool _ownsClient;

    public HealthCheckService(HttpClient? client = null, TimeProvider? clock = null)
    {
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsClient = client is null;
        _clock = clock ?? TimeProvider.System;
    }

    public event Action<HealthCheckResult>? ResultChanged;

    public IReadOnlyList<HealthCheckResult> History(Guid endpointId)
    {
        lock (_gate) return _history.TryGetValue(endpointId, out var list) ? list.ToArray() : [];
    }

    public void EnsureDefinition(HealthEndpoint endpoint)
    {
        lock (_gate)
        {
            if (_definitions.TryGetValue(endpoint.Id, out var previous) && previous != endpoint)
            {
                _history.Remove(endpoint.Id);
                _inFlight.Remove(endpoint.Id);
            }
            _definitions[endpoint.Id] = endpoint;
        }
    }

    /// <summary>같은 앱에 대한 자동·수동 요청은 진행 중인 한 HTTP 작업을 공유한다.</summary>
    public Task<HealthCheckResult> CheckAsync(HealthEndpoint endpoint, int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!new[] { 3, 5, 10, 15 }.Contains(timeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        EnsureDefinition(endpoint);
        lock (_gate)
        {
            if (_inFlight.TryGetValue(endpoint.Id, out var current)) return current.WaitAsync(cancellationToken);
            var task = RunAsync(endpoint, timeoutSeconds);
            _inFlight[endpoint.Id] = task;
            return task.WaitAsync(cancellationToken);
        }
    }

    private async Task<HealthCheckResult> RunAsync(HealthEndpoint endpoint, int timeoutSeconds)
    {
        await Task.Yield();
        HealthCheckResult result;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            // 등록된 URL에 GET을 보내 애플리케이션 응답을 확인한다. AWS 인스턴스 상태 조회와 독립적이다.
            using var response = await _client.GetAsync(endpoint.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            if (code is < 200 or >= 300)
                result = NewResult(endpoint.Id, HealthCheckState.Unhealthy, $"HTTP {code}", code);
            else if (endpoint.Rule == HealthCheckRule.Http2xx)
                result = NewResult(endpoint.Id, HealthCheckState.Healthy, $"HTTP {code}", code);
            else
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                var valid = endpoint.Rule == HealthCheckRule.StatusUp &&
                    string.Equals(body.Trim(), "status=UP", StringComparison.Ordinal);
                if (!valid)
                {
                    using var document = JsonDocument.Parse(body);
                    valid = endpoint.Rule == HealthCheckRule.Json ||
                        document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("status", out var status) &&
                        status.ValueKind == JsonValueKind.String && status.GetString() == "UP";
                }
                result = NewResult(endpoint.Id, valid ? HealthCheckState.Healthy : HealthCheckState.Unhealthy,
                    valid ? $"HTTP {code} · 본문 판정 정상" : "본문의 status가 UP이 아닙니다.", code);
            }
        }
        catch (OperationCanceledException)
        {
            result = NewResult(endpoint.Id, HealthCheckState.ConnectionFailed, "응답 시간 초과");
        }
        catch (HttpRequestException exception)
        {
            result = NewResult(endpoint.Id, HealthCheckState.ConnectionFailed, $"연결 실패: {exception.Message}");
        }
        catch (JsonException)
        {
            result = NewResult(endpoint.Id, HealthCheckState.Unhealthy, "JSON 응답이 올바르지 않습니다.");
        }
        catch (Exception exception)
        {
            result = NewResult(endpoint.Id, HealthCheckState.Unknown, $"확인 불가: {exception.GetType().Name}");
        }
        var publish = false;
        lock (_gate)
        {
            if (_definitions.TryGetValue(endpoint.Id, out var current) && current == endpoint)
            {
                _inFlight.Remove(endpoint.Id);
                if (!_history.TryGetValue(endpoint.Id, out var list)) _history[endpoint.Id] = list = [];
                list.Insert(0, result);
                if (list.Count > 10) list.RemoveRange(10, list.Count - 10);
                publish = true;
            }
        }
        if (publish) ResultChanged?.Invoke(result);
        return result;
    }

    private HealthCheckResult NewResult(Guid id, HealthCheckState state, string message, int? code = null) =>
        new(id, _clock.GetUtcNow(), state, message, code);

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
