using PersonalServerManager.Domain.Models;

namespace PersonalServerManager.Domain.Rules;

/// <summary>그룹 이름, 자원 식별, 시작 순서의 일관성을 검사한다.</summary>
public static class GroupRules
{
    public static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 50 ||
            normalized.Any(character => !IsAllowedNameCharacter(character)))
        {
            throw new ServiceGroupException("그룹 이름은 1~50자의 한글, 영문, 숫자, 공백, -, _, .만 사용할 수 있습니다.");
        }

        return normalized;
    }

    public static void Validate(ServiceGroup group)
    {
        if (group.Id == Guid.Empty)
        {
            throw new ServiceGroupException("그룹 ID가 올바르지 않습니다.");
        }

        if (!string.Equals(group.Name, NormalizeName(group.Name), StringComparison.Ordinal))
        {
            throw new ServiceGroupException("그룹 이름 앞뒤의 공백을 제거해야 합니다.");
        }

        if (group.AccountId is not { Length: 12 } accountId || !accountId.All(char.IsAsciiDigit))
        {
            throw new ServiceGroupException("AWS 계정 ID는 12자리 숫자여야 합니다.");
        }

        if (string.IsNullOrWhiteSpace(group.Region) || group.Region != group.Region.Trim() ||
            string.IsNullOrWhiteSpace(group.ProfileName) ||
            group.CreatedAt == default || group.UpdatedAt < group.CreatedAt)
        {
            throw new ServiceGroupException("그룹의 리전, 프로필 또는 생성 시각이 올바르지 않습니다.");
        }

        if (group.Resources is null || group.StartOrder is null)
        {
            throw new ServiceGroupException("그룹의 리소스 또는 시작 순서가 없습니다.");
        }

        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var controllable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in group.Resources)
        {
            if (resource is null || !Enum.IsDefined(resource.Kind) ||
                string.IsNullOrWhiteSpace(resource.Id) || resource.Id != resource.Id.Trim() ||
                resource.Name is null || string.IsNullOrWhiteSpace(resource.Region) ||
                resource.Region != resource.Region.Trim())
            {
                throw new ServiceGroupException("등록된 AWS 리소스 정보가 올바르지 않습니다.");
            }

            if (resource.Kind != AwsResourceKind.S3 &&
                !string.Equals(resource.Region, group.Region, StringComparison.OrdinalIgnoreCase))
            {
                throw new ServiceGroupException("EC2/RDS 리소스의 리전이 그룹 리전과 다릅니다.");
            }

            var identity = ResourceIdentity(group.AccountId, group.Region, resource.Kind, resource.Id);
            if (!resources.Add(identity))
            {
                throw new ServiceGroupException("한 그룹에 같은 AWS 리소스를 두 번 등록할 수 없습니다.");
            }

            if (resource.Kind != AwsResourceKind.S3)
            {
                controllable.Add(OrderIdentity(resource.Kind, resource.Id));
            }
        }

        var ordered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in group.StartOrder)
        {
            if (reference is null || reference.Kind == AwsResourceKind.S3 ||
                !Enum.IsDefined(reference.Kind) || string.IsNullOrWhiteSpace(reference.Id) ||
                reference.Id != reference.Id.Trim())
            {
                throw new ServiceGroupException("시작 순서에는 EC2/RDS 리소스만 넣을 수 있습니다.");
            }

            var identity = OrderIdentity(reference.Kind, reference.Id);
            if (!controllable.Contains(identity) || !ordered.Add(identity))
            {
                throw new ServiceGroupException("시작 순서에 미등록 또는 중복 리소스가 있습니다.");
            }
        }

        if (ordered.Count != controllable.Count)
        {
            throw new ServiceGroupException("모든 EC2/RDS 리소스의 시작 순서를 지정해야 합니다.");
        }
    }

    public static void EnsureUnique(ServiceGroup candidate, IEnumerable<ServiceGroup> existingGroups)
    {
        var candidateResources = candidate.Resources
            .Select(resource => ResourceIdentity(candidate.AccountId, candidate.Region, resource.Kind, resource.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var existing in existingGroups)
        {
            if (existing.Id == candidate.Id)
            {
                throw new ServiceGroupException("같은 그룹 ID가 이미 등록되어 있습니다.");
            }

            if (string.Equals(existing.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ServiceGroupException("같은 이름의 서비스 그룹이 이미 있습니다.");
            }

            if (existing.Resources.Any(resource => candidateResources.Contains(
                ResourceIdentity(existing.AccountId, existing.Region, resource.Kind, resource.Id))))
            {
                throw new ServiceGroupException("선택한 AWS 리소스가 다른 서비스 그룹에 이미 등록되어 있습니다.");
            }
        }
    }

    private static string ResourceIdentity(string accountId, string groupRegion, AwsResourceKind kind, string resourceId) =>
        $"{accountId}|{kind}|{(kind == AwsResourceKind.S3 ? "" : groupRegion)}|{resourceId}";

    private static string OrderIdentity(AwsResourceKind kind, string resourceId) => $"{kind}|{resourceId}";

    private static bool IsAllowedNameCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is ' ' or '-' or '_' or '.' ||
        character is >= '\uAC00' and <= '\uD7A3' ||
        character is >= '\u1100' and <= '\u11FF' ||
        character is >= '\u3130' and <= '\u318F';
}
