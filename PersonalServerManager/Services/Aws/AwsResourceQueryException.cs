namespace PersonalServerManager.Services.Aws;

public enum AwsQueryFailureKind
{
    Authentication,
    ExpiredSession,
    AccessDenied,
    Network,
    Other
}

public sealed class AwsResourceQueryException(AwsQueryFailureKind kind, string message) : Exception(message)
{
    public AwsQueryFailureKind Kind { get; } = kind;
}
