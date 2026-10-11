namespace PersonalServerManager.Application.Models;

/// <summary>STS 인증 결과 중 화면과 계정 검증에 필요한 값만 담는다.</summary>
public sealed record AwsIdentity(string AccountId, string CallerArn);
