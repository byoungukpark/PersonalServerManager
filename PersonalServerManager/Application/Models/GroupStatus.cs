using PersonalServerManager.Domain.Models;

namespace PersonalServerManager.Application.Models;

public sealed record ServiceReadResult(
    AwsResourceKind Kind,
    IReadOnlyDictionary<string, string> States,
    DateTimeOffset? ReadAt,
    string? Error);

public sealed record GroupReadResult(string? AccessMessage, IReadOnlyList<ServiceReadResult> Services);
