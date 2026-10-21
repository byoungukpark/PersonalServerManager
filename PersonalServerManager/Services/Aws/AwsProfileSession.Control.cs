using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.RDS;
using Amazon.RDS.Model;
using Amazon.Runtime;
using PersonalServerManager.Models;

namespace PersonalServerManager.Services.Aws;

internal sealed partial class AwsProfileSession
{
    /// <summary>목록 캐시 대신 대상 식별자로 조회해 명령 직전 상태·지원 여부를 확인한다.</summary>
    public async Task<ResourceControlSnapshot> ReadControlStateAsync(
        string region, AwsResourceKind kind, string id, CancellationToken cancellationToken)
    {
        try
        {
            if (kind == AwsResourceKind.Ec2)
            {
                using var client = new AmazonEC2Client(_credentials, RegionEndpoint.GetBySystemName(region));
                // DescribeInstances: 등록된 단일 EC2의 상태와 EBS 루트 여부를 확인한다.
                var response = await client.DescribeInstancesAsync(
                    new DescribeInstancesRequest { InstanceIds = [id] }, cancellationToken);
                var instance = response.Reservations.SelectMany(item => item.Instances).SingleOrDefault();
                if (instance is null) return new("Unknown", "EC2 인스턴스를 찾을 수 없습니다.");
                var reason = instance.RootDeviceType?.Value != "ebs"
                    ? "EBS 루트 볼륨을 사용하는 EC2만 제어합니다." : null;
                return new(instance.State?.Name?.Value ?? "Unknown", StartStopBlockReason: reason);
            }
            if (kind == AwsResourceKind.Rds)
            {
                using var client = new AmazonRDSClient(_credentials, RegionEndpoint.GetBySystemName(region));
                // DescribeDBInstances: 클러스터·복제본·SQL Server Multi-AZ 등 미지원 구성을 차단한다.
                var response = await client.DescribeDBInstancesAsync(
                    new DescribeDBInstancesRequest { DBInstanceIdentifier = id }, cancellationToken);
                var instance = response.DBInstances.SingleOrDefault();
                if (instance is null) return new("Unknown", "RDS 인스턴스를 찾을 수 없습니다.");
                string? reason = null;
                if (!string.IsNullOrEmpty(instance.DBClusterIdentifier))
                    reason = "클러스터 소속 RDS의 개별 시작·중지는 지원하지 않습니다.";
                else if (!string.IsNullOrEmpty(instance.ReadReplicaSourceDBInstanceIdentifier) ||
                         instance.ReadReplicaDBInstanceIdentifiers?.Count > 0)
                    reason = "읽기 복제본 또는 복제본이 있는 RDS는 시작·중지 대상에서 제외합니다.";
                else if (instance.Engine?.StartsWith("custom-", StringComparison.OrdinalIgnoreCase) == true)
                    reason = "RDS Custom은 이번 개별 제어 범위에서 지원하지 않습니다.";
                else if (instance.Engine?.StartsWith("sqlserver", StringComparison.OrdinalIgnoreCase) == true &&
                         instance.MultiAZ == true)
                    reason = "SQL Server Multi-AZ RDS는 중지를 지원하지 않습니다.";
                return new(instance.DBInstanceStatus ?? "Unknown", reason);
            }
            return new("Unknown", "S3는 제어 대상이 아닙니다.");
        }
        catch (AmazonServiceException exception)
        {
            throw MapControlError(exception);
        }
        catch (AmazonClientException)
        {
            throw new AwsResourceQueryException(AwsQueryFailureKind.Network,
                "AWS 상태 조회에 실패했습니다. 네트워크와 권한을 확인하세요.");
        }
    }

    public async Task SendControlAsync(string region, AwsResourceKind kind, string id,
        ResourceControlAction action, CancellationToken cancellationToken)
    {
        try
        {
            if (kind == AwsResourceKind.Ec2)
            {
                // 제어 API의 자동 재시도는 끈다. 통신 실패 시 이미 접수됐을 가능성을 안내한다.
                using var client = new AmazonEC2Client(_credentials,
                    new AmazonEC2Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region), MaxErrorRetry = 0 });
                switch (action)
                {
                    case ResourceControlAction.Start:
                        // StartInstances: 선택한 등록 EC2 한 대의 시작을 요청한다.
                        await client.StartInstancesAsync(new StartInstancesRequest { InstanceIds = [id] }, cancellationToken);
                        break;
                    case ResourceControlAction.Stop:
                        // StopInstances: 강제 중지 옵션 없이 일반 중지를 요청한다.
                        await client.StopInstancesAsync(new StopInstancesRequest { InstanceIds = [id] }, cancellationToken);
                        break;
                    case ResourceControlAction.Reboot:
                        // RebootInstances는 비동기 요청 접수이며 running만으로 재부팅 완료를 판정할 수 없다.
                        await client.RebootInstancesAsync(new RebootInstancesRequest { InstanceIds = [id] }, cancellationToken);
                        break;
                    default: throw new InvalidOperationException("지원하지 않는 명령입니다.");
                }
                return;
            }
            if (kind == AwsResourceKind.Rds && action != ResourceControlAction.Reboot)
            {
                using var client = new AmazonRDSClient(_credentials,
                    new AmazonRDSConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region), MaxErrorRetry = 0 });
                if (action == ResourceControlAction.Start)
                {
                    // StartDBInstance: 등록된 단일 DB 인스턴스의 시작 요청이다.
                    await client.StartDBInstanceAsync(new StartDBInstanceRequest { DBInstanceIdentifier = id }, cancellationToken);
                }
                else
                {
                    // StopDBInstance: 스냅샷 생성이나 삭제 없이 일반 중지를 요청한다.
                    await client.StopDBInstanceAsync(new StopDBInstanceRequest { DBInstanceIdentifier = id }, cancellationToken);
                }
                return;
            }
            throw new InvalidOperationException("지원하지 않는 리소스 또는 명령입니다.");
        }
        catch (AmazonServiceException exception)
        {
            throw MapControlError(exception);
        }
        catch (AmazonClientException)
        {
            throw new AwsResourceQueryException(AwsQueryFailureKind.Network,
                "AWS 제어 응답을 확인하지 못했습니다. 요청이 접수되었을 수 있으므로 실제 상태를 확인하세요.");
        }
    }

    private static AwsResourceQueryException MapControlError(AmazonServiceException exception)
    {
        var mapped = MapQueryError(exception);
        var message = mapped.Kind == AwsQueryFailureKind.AccessDenied
            ? "AWS 제어 또는 상태 조회 권한이 없습니다. 권한 정책을 확인하세요."
            : mapped.Kind is AwsQueryFailureKind.Authentication or AwsQueryFailureKind.ExpiredSession
                ? mapped.Message
                : $"AWS 요청이 거부되었거나 응답을 확인하지 못했습니다 ({exception.ErrorCode ?? "Unknown"}). 실제 상태를 확인하세요.";
        return new AwsResourceQueryException(mapped.Kind, message);
    }
}
