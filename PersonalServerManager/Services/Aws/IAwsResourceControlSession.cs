using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

public enum ResourceControlAction { Start, Stop, Reboot }

/// <summary>명령 직전 조회한 현재 상태와 제어 불가 사유다.</summary>
public sealed record ResourceControlSnapshot(string State, string? BlockReason = null,
    string? StartStopBlockReason = null);

/// <summary>인증된 세션의 단일 자원 조회·명령 경계. S3는 제어하지 않는다.</summary>
public interface IAwsResourceControlSession
{
    Task<ResourceControlSnapshot> ReadControlStateAsync(
        string region, AwsResourceKind kind, string id, CancellationToken cancellationToken);
    Task SendControlAsync(string region, AwsResourceKind kind, string id,
        ResourceControlAction action, CancellationToken cancellationToken);
}
