namespace PersonalServerManager.Application.Models;

/// <summary>STS에서 확인한 현재 활성 프로필의 공개 식별 정보다.</summary>
public sealed record AwsConnectionInfo(
    string ProfileName,
    string AccountId,
    string CallerArn,
    DateTimeOffset LastVerifiedAt);
