using PersonalServerManager.Application.Contracts;
using PersonalServerManager.Application.Models;

namespace PersonalServerManager.Application.Services;

/// <summary>현재 연결한 AWS 계정의 리소스 목록을 읽는다.</summary>
public sealed class AwsResourceQueryService : IAwsResourceQuery
{
    private readonly AwsConnectionService _connection;

    public AwsResourceQueryService(AwsConnectionService connection)
    {
        _connection = connection;
    }

    public async Task<IReadOnlyList<Ec2InstanceResource>> ListEc2Async(
        string region, CancellationToken cancellationToken)
    {
        var session = GetActiveSession(out var connectionVersion);

        var instances = await session.ListEc2InstancesAsync(region, cancellationToken);
        EnsureConnectionUnchanged(session, connectionVersion, cancellationToken);
        return instances;
    }

    public async Task<IReadOnlyList<RdsInstanceResource>> ListRdsAsync(
        string region, CancellationToken cancellationToken)
    {
        var session = GetActiveSession(out var connectionVersion);

        var instances = await session.ListRdsInstancesAsync(region, cancellationToken);
        EnsureConnectionUnchanged(session, connectionVersion, cancellationToken);
        return instances;
    }

    public async Task<IReadOnlyList<S3BucketResource>> ListS3Async(CancellationToken cancellationToken)
    {
        var session = GetActiveSession(out var connectionVersion);

        var buckets = await session.ListS3BucketsAsync(cancellationToken);
        EnsureConnectionUnchanged(session, connectionVersion, cancellationToken);
        return buckets;
    }

    private IAwsProfileSession GetActiveSession(out long connectionVersion)
    {
        var activeConnection = _connection.ActiveConnection;
        var activeSession = _connection.ActiveSession;

        if (activeConnection is null || activeSession is null)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.ProfileMissing,
                "AWS 연결 관리에서 프로필을 먼저 연결하세요.");
        }

        connectionVersion = activeConnection.ConnectionVersion;
        return activeSession;
    }

    /// <summary>프로필을 바꾸거나 연결을 해제한 뒤 도착한 이전 조회 결과를 버린다.</summary>
    private void EnsureConnectionUnchanged(
        IAwsProfileSession originalSession,
        long originalConnectionVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ReferenceEquals(originalSession, _connection.ActiveSession) ||
            originalConnectionVersion != _connection.ActiveConnection?.ConnectionVersion)
        {
            throw new AwsConnectionException(
                AwsConnectionFailureKind.Busy,
                "조회 중 AWS 연결이 변경되어 이전 연결의 결과는 표시하지 않았습니다.");
        }
    }
}
