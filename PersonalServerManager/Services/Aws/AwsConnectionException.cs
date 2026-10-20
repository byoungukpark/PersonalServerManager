namespace PersonalServerManager.Services.Aws;

public enum AwsConnectionFailureKind
{
    ProfileMissing,
    UnsupportedProfile,
    Authentication,
    ExpiredSession,
    AccessDenied,
    Network,
    Busy,
    Other
}

/// <summary>민감한 SDK 예외 원문 대신 화면에 표시 가능한 연결 오류다.</summary>
public sealed class AwsConnectionException(
    AwsConnectionFailureKind kind,
    string message) : Exception(message)
{
    public AwsConnectionFailureKind Kind { get; } = kind;
}
