namespace PersonalServerManager.Domain.Models;

public enum AwsResourceKind
{
    Ec2,
    Rds,
    S3
}

/// <summary>그룹에 연결한 AWS 자원의 식별 정보다. S3는 버킷 고유 리전을 보존한다.</summary>
public sealed record GroupResource(AwsResourceKind Kind, string Id, string Name, string Region = "");

public sealed record GroupResourceRef(AwsResourceKind Kind, string Id);

/// <summary>로컬에 저장하는 서비스 그룹 구성이다. 활성 인증 정보는 포함하지 않는다.</summary>
public sealed record ServiceGroup(
    Guid Id,
    string Name,
    string AccountId,
    string Region,
    string ProfileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<GroupResource> Resources,
    IReadOnlyList<GroupResourceRef> StartOrder);
