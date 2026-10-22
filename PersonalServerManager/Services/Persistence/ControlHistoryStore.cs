using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace PersonalServerManager.Services.Persistence;

public sealed record ControlJobRecord(Guid Id, Guid GroupId, string GroupName, string AccountId,
    string Region, string Action, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string Status, string Message)
{
    public DateTimeOffset StartedLocal => StartedAt.ToLocalTime();
}

public sealed record ControlStepRecord(Guid JobId, int Position, string Kind, string ResourceId,
    string Action, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    bool RequestAccepted, bool GoalConfirmed, string Status, string Message)
{
    public DateTimeOffset? StartedLocal => StartedAt?.ToLocalTime();
    public DateTimeOffset? CompletedLocal => CompletedAt?.ToLocalTime();
}

public interface IControlHistoryStore
{
    bool IsAvailable { get; }
    string FailureMessage { get; }
    event Action? AvailabilityChanged;
    Task InitializeAsync(int retentionDays, CancellationToken token);
    Task StartAsync(ControlJobRecord job, IReadOnlyList<ControlStepRecord> steps, CancellationToken token);
    Task UpdateStepAsync(ControlStepRecord step, CancellationToken token);
    Task FinishAsync(ControlJobRecord job, CancellationToken token);
    Task<IReadOnlyList<ControlJobRecord>> SearchAsync(string keyword, string status, CancellationToken token);
    Task<IReadOnlyList<ControlStepRecord>> StepsAsync(Guid jobId, CancellationToken token);
}

/// <summary>작업과 자원별 단계를 로컬 SQLite에 저장한다. 기록 오류가 나면 새 제어를 차단한다.</summary>
public sealed class ControlHistoryStore : IControlHistoryStore
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsAvailable { get; private set; }
    public string FailureMessage { get; private set; } = "작업 기록 저장소가 준비되지 않았습니다. 제어를 시작할 수 없습니다.";
    public event Action? AvailabilityChanged;

    public ControlHistoryStore(string? path = null, TimeProvider? clock = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _path = path ?? Path.Combine(local, "PersonalServerManager", "control-history.db");
        _clock = clock ?? TimeProvider.System;
    }

    public async Task InitializeAsync(int retentionDays, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var connection = await OpenAsync(token);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS ControlJobs (
                    Id TEXT PRIMARY KEY, GroupId TEXT NOT NULL, GroupName TEXT NOT NULL,
                    AccountId TEXT NOT NULL, Region TEXT NOT NULL, Action TEXT NOT NULL,
                    StartedAt TEXT NOT NULL, CompletedAt TEXT, Status TEXT NOT NULL, Message TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS ControlSteps (
                    JobId TEXT NOT NULL, Position INTEGER NOT NULL, Kind TEXT NOT NULL,
                    ResourceId TEXT NOT NULL, Action TEXT NOT NULL, StartedAt TEXT, CompletedAt TEXT,
                    RequestAccepted INTEGER NOT NULL, GoalConfirmed INTEGER NOT NULL,
                    Status TEXT NOT NULL, Message TEXT NOT NULL,
                    PRIMARY KEY (JobId, Position),
                    FOREIGN KEY (JobId) REFERENCES ControlJobs(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_ControlJobs_StartedAt ON ControlJobs(StartedAt DESC);
                CREATE INDEX IF NOT EXISTS IX_ControlJobs_GroupId ON ControlJobs(GroupId);
                CREATE INDEX IF NOT EXISTS IX_ControlSteps_JobId ON ControlSteps(JobId);
                """, token);
            using var tx = connection.BeginTransaction();
            // 이전 실행에서 중단된 작업은 성공으로 바꾸거나 자동 재실행하지 않는다.
            await ExecuteAsync(connection, """
                UPDATE ControlSteps SET Status='미실행',
                    Message='앱 종료 전에 이 단계의 명령을 보내지 않았습니다.'
                WHERE JobId IN (SELECT Id FROM ControlJobs WHERE Status='진행 중')
                    AND Status='대기' AND RequestAccepted=0;
                UPDATE ControlSteps SET Status='완료 미확인',
                    Message='앱 종료 전 완료 여부를 확인하지 못했습니다.'
                WHERE JobId IN (SELECT Id FROM ControlJobs WHERE Status='진행 중')
                    AND Status IN ('진행 중','명령 전송','AWS 요청 접수','목표 상태 대기');
                UPDATE ControlJobs SET Status='완료 미확인',
                    Message='앱 종료 전 완료 여부를 확인하지 못했습니다.',
                    CompletedAt=$now WHERE Status='진행 중';
                """, token, tx, ("$now", NowText(_clock.GetUtcNow())));
            if (retentionDays > 0)
                await ExecuteAsync(connection, """
                    DELETE FROM ControlJobs WHERE Status <> '진행 중' AND
                        COALESCE(CompletedAt, StartedAt) < $cutoff;
                    """, token, tx, ("$cutoff", NowText(_clock.GetUtcNow().AddDays(-retentionDays))));
            tx.Commit();
            SetAvailable();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkUnavailable(exception);
            throw new AppSettingsException(FailureMessage);
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync(ControlJobRecord job, IReadOnlyList<ControlStepRecord> steps, CancellationToken token)
    {
        await WriteAsync(async connection =>
        {
            using var tx = connection.BeginTransaction();
            await ExecuteAsync(connection, """
                INSERT INTO ControlJobs VALUES ($id,$groupId,$groupName,$account,$region,$action,
                    $started,$completed,$status,$message);
                """, token, tx, ("$id", job.Id.ToString()), ("$groupId", job.GroupId.ToString()),
                ("$groupName", job.GroupName), ("$account", job.AccountId), ("$region", job.Region),
                ("$action", job.Action), ("$started", NowText(job.StartedAt)),
                ("$completed", job.CompletedAt is null ? DBNull.Value : NowText(job.CompletedAt.Value)),
                ("$status", job.Status), ("$message", job.Message));
            foreach (var step in steps)
                await InsertStepAsync(connection, tx, step, token);
            tx.Commit();
        }, token);
    }

    public Task UpdateStepAsync(ControlStepRecord step, CancellationToken token) => WriteAsync(async connection =>
    {
        var changed = await ExecuteAsync(connection, """
            UPDATE ControlSteps SET StartedAt=$started, CompletedAt=$completed,
                RequestAccepted=$accepted, GoalConfirmed=$confirmed, Status=$status, Message=$message
            WHERE JobId=$jobId AND Position=$position;
            """, token, null, ("$started", step.StartedAt is null ? DBNull.Value : NowText(step.StartedAt.Value)),
            ("$completed", step.CompletedAt is null ? DBNull.Value : NowText(step.CompletedAt.Value)),
            ("$accepted", step.RequestAccepted ? 1 : 0), ("$confirmed", step.GoalConfirmed ? 1 : 0),
            ("$status", step.Status), ("$message", step.Message),
            ("$jobId", step.JobId.ToString()), ("$position", step.Position));
        if (changed != 1) throw new AppSettingsException("기록할 단계를 찾지 못했습니다.");
    }, token);

    public Task FinishAsync(ControlJobRecord job, CancellationToken token) => WriteAsync(async connection =>
    {
        var changed = await ExecuteAsync(connection, """
            UPDATE ControlJobs SET CompletedAt=$completed, Status=$status, Message=$message WHERE Id=$id;
            """, token, null, ("$completed", job.CompletedAt is null ? DBNull.Value : NowText(job.CompletedAt.Value)),
            ("$status", job.Status), ("$message", job.Message), ("$id", job.Id.ToString()));
        if (changed != 1) throw new AppSettingsException("기록할 작업을 찾지 못했습니다.");
    }, token);

    public async Task<IReadOnlyList<ControlJobRecord>> SearchAsync(string keyword, string status, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id,GroupId,GroupName,AccountId,Region,Action,StartedAt,CompletedAt,Status,Message
                FROM ControlJobs j WHERE ($status='' OR j.Status=$status)
                    AND ($keyword='' OR j.GroupName LIKE $pattern OR j.Action LIKE $pattern OR
                        j.Message LIKE $pattern OR EXISTS (SELECT 1 FROM ControlSteps s
                            WHERE s.JobId=j.Id AND (s.ResourceId LIKE $pattern OR s.Message LIKE $pattern)))
                ORDER BY j.StartedAt DESC LIMIT 200;
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$keyword", keyword);
            command.Parameters.AddWithValue("$pattern", $"%{keyword}%");
            var result = new List<ControlJobRecord>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                result.Add(new ControlJobRecord(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    ParseTime(reader.GetString(6)), reader.IsDBNull(7) ? null : ParseTime(reader.GetString(7)),
                    reader.GetString(8), reader.GetString(9)));
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { MarkUnavailable(exception); throw new AppSettingsException(FailureMessage); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ControlStepRecord>> StepsAsync(Guid jobId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await using var connection = await OpenAsync(token);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT JobId,Position,Kind,ResourceId,Action,StartedAt,CompletedAt,
                    RequestAccepted,GoalConfirmed,Status,Message
                FROM ControlSteps WHERE JobId=$jobId ORDER BY Position;
                """;
            command.Parameters.AddWithValue("$jobId", jobId.ToString());
            var result = new List<ControlStepRecord>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                result.Add(new ControlStepRecord(Guid.Parse(reader.GetString(0)), reader.GetInt32(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6)),
                    reader.GetInt32(7) != 0, reader.GetInt32(8) != 0, reader.GetString(9), reader.GetString(10)));
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { MarkUnavailable(exception); throw new AppSettingsException(FailureMessage); }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(Func<SqliteConnection, Task> write, CancellationToken token)
    {
        if (!IsAvailable) throw new AppSettingsException(FailureMessage);
        await _gate.WaitAsync(token);
        try
        {
            if (!IsAvailable) throw new AppSettingsException(FailureMessage);
            await using var connection = await OpenAsync(token);
            await write(connection);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { MarkUnavailable(exception); throw new AppSettingsException(FailureMessage); }
        finally { _gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private void SetAvailable()
    {
        IsAvailable = true;
        FailureMessage = "";
        AvailabilityChanged?.Invoke();
    }

    private void MarkUnavailable(Exception exception)
    {
        IsAvailable = false;
        FailureMessage = $"작업 기록을 저장하거나 읽지 못했습니다 ({exception.GetType().Name}). 새 제어를 중단하고 DB 파일을 확인하세요.";
        AvailabilityChanged?.Invoke();
    }

    private static string NowText(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static async Task InsertStepAsync(SqliteConnection connection, SqliteTransaction tx,
        ControlStepRecord step, CancellationToken token)
    {
        await ExecuteAsync(connection, """
            INSERT INTO ControlSteps VALUES ($jobId,$position,$kind,$resourceId,$action,$started,
                $completed,$accepted,$confirmed,$status,$message);
            """, token, tx, ("$jobId", step.JobId.ToString()), ("$position", step.Position),
            ("$kind", step.Kind), ("$resourceId", step.ResourceId), ("$action", step.Action),
            ("$started", step.StartedAt is null ? DBNull.Value : NowText(step.StartedAt.Value)),
            ("$completed", step.CompletedAt is null ? DBNull.Value : NowText(step.CompletedAt.Value)),
            ("$accepted", step.RequestAccepted ? 1 : 0), ("$confirmed", step.GoalConfirmed ? 1 : 0),
            ("$status", step.Status), ("$message", step.Message));
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, CancellationToken token,
        SqliteTransaction? tx = null, params (string Name, object Value)[] args)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync(token);
    }
}
