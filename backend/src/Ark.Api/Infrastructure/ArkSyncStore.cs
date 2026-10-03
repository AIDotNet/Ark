using System.Text.Json;
using System.Text.Json.Serialization;
using Ark.Core.Sync;
using Ark.Sync;
using Microsoft.Data.Sqlite;

namespace Ark.Api.Infrastructure;

/// <summary>ark.db 上的同步任务/Profile 持久化实现。</summary>
public sealed class ArkSyncStore(ArkRepository repo) : ISyncTaskStore, ISyncProfileStore
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>可完整 JSON roundtrip 的任务快照（SyncTaskState 的 Log/Reports 是 get-only，需展开）。</summary>
    private sealed record TaskBlob(
        Guid Id, string Kind, Guid? ProfileId, string Status, string? CurrentTable,
        double Percent, string? Message, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
        string? Error, long RowsDone, long RowsTotal,
        List<string> Log, List<TableSyncReport> Reports, CompareResult? Compare,
        ExecutionPlan? Plan, SyncExecuteRequest? Request, CompareRequest? CompareReq,
        SyncCheckpoint Checkpoint);

    // ------------------------------------------------ 任务 ------------------------------------------------

    public async Task SaveTaskAsync(SyncTaskState s, CancellationToken ct = default)
    {
        var blob = new TaskBlob(
            s.Id, s.Kind.ToString(), s.ProfileId, s.Status.ToString(), s.CurrentTable,
            s.Percent, s.Message, s.StartedAt, s.FinishedAt, s.Error, s.RowsDone, s.RowsTotal,
            s.Log, s.Reports, s.Compare, s.Plan, s.Request, s.CompareRequestRef, s.Checkpoint);
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sync_tasks (id, kind, profile_id, status, started_at, finished_at, updated_at, state_json)
            VALUES (@id, @kind, @profile_id, @status, @started_at, @finished_at, @updated_at, @state_json)
            """;
        cmd.Parameters.AddWithValue("@id", s.Id.ToString());
        cmd.Parameters.AddWithValue("@kind", s.Kind.ToString());
        cmd.Parameters.AddWithValue("@profile_id", (object?)s.ProfileId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", s.Status.ToString());
        cmd.Parameters.AddWithValue("@started_at", s.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@finished_at", (s.FinishedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        cmd.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@state_json", JsonSerializer.Serialize(blob, JsonOpts));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<StoredTask?> GetTaskAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT state_json FROM sync_tasks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? null : FromBlob(JsonSerializer.Deserialize<TaskBlob>(json, JsonOpts));
    }

    public async Task<IReadOnlyList<StoredTask>> ListTasksAsync(
        TaskKind? kind, SyncTaskStatus? status, Guid? profileId, int limit, int offset, CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        var conds = new List<string>();
        if (kind is not null) { conds.Add("kind = @kind"); cmd.Parameters.AddWithValue("@kind", kind.ToString()); }
        if (status is not null) { conds.Add("status = @status"); cmd.Parameters.AddWithValue("@status", status.ToString()); }
        if (profileId is not null) { conds.Add("profile_id = @pid"); cmd.Parameters.AddWithValue("@pid", profileId.ToString()); }
        cmd.CommandText =
            "SELECT state_json FROM sync_tasks" +
            (conds.Count > 0 ? " WHERE " + string.Join(" AND ", conds) : "") +
            " ORDER BY started_at DESC LIMIT @l OFFSET @o";
        cmd.Parameters.AddWithValue("@l", Math.Clamp(limit, 1, 200));
        cmd.Parameters.AddWithValue("@o", Math.Max(0, offset));
        var list = new List<StoredTask>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var blob = JsonSerializer.Deserialize<TaskBlob>(r.GetString(0), JsonOpts);
            if (blob is not null) list.Add(FromBlob(blob)!);
        }
        return list;
    }

    public async Task MarkStartupInterruptedAsync(CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, state_json FROM sync_tasks WHERE status IN ('Running','Queued','CancelRequested')";
        var updates = new List<(string Id, string Json)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                updates.Add((r.GetString(0), r.GetString(1)));
        }
        foreach (var (id, json) in updates)
        {
            var blob = JsonSerializer.Deserialize<TaskBlob>(json, JsonOpts);
            if (blob is null) continue;
            var state = ToState(blob);
            state.Status = SyncTaskStatus.Interrupted;
            state.Message = "服务重启导致中断，可续传或重新执行";
            state.FinishedAt = DateTimeOffset.UtcNow;
            blob = blob with { Status = "Interrupted", Message = state.Message, FinishedAt = state.FinishedAt };
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "UPDATE sync_tasks SET status='Interrupted', state_json=@j, updated_at=@u WHERE id=@id";
            cmd2.Parameters.AddWithValue("@j", JsonSerializer.Serialize(blob, JsonOpts));
            cmd2.Parameters.AddWithValue("@u", DateTimeOffset.UtcNow.ToString("O"));
            cmd2.Parameters.AddWithValue("@id", id);
            await cmd2.ExecuteNonQueryAsync(ct);
            _ = state;
        }
    }

    public async Task<int> CountActiveByProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sync_tasks WHERE profile_id = @pid AND status IN ('Queued','Running','CancelRequested')";
        cmd.Parameters.AddWithValue("@pid", profileId.ToString());
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt32(v);
    }

    private static StoredTask? FromBlob(TaskBlob? b)
    {
        if (b is null) return null;
        var state = ToState(b);
        return new StoredTask
        {
            State = state,
            Plan = b.Plan,
            Request = b.Request,
            CompareReq = b.CompareReq,
            Checkpoint = b.Checkpoint ?? SyncCheckpoint.None,
        };
    }

    private static SyncTaskState ToState(TaskBlob b)
    {
        var status = Enum.TryParse<SyncTaskStatus>(b.Status, out var st) ? st : SyncTaskStatus.Failed;
        var kind = Enum.TryParse<TaskKind>(b.Kind, out var kd) ? kd : TaskKind.Sync;
        var state = new SyncTaskState
        {
            Id = b.Id,
            Kind = kind,
            ProfileId = b.ProfileId,
            Status = status,
            CurrentTable = b.CurrentTable,
            Percent = b.Percent,
            Message = b.Message,
            StartedAt = b.StartedAt,
            FinishedAt = b.FinishedAt,
            Error = b.Error,
            RowsDone = b.RowsDone,
            RowsTotal = b.RowsTotal,
            Compare = b.Compare,
            Plan = b.Plan,
            Request = b.Request,
            CompareRequestRef = b.CompareReq,
            Checkpoint = b.Checkpoint ?? SyncCheckpoint.None,
        };
        if (b.Log is { Count: > 0 }) state.Log.AddRange(b.Log);
        if (b.Reports is { Count: > 0 }) state.Reports.AddRange(b.Reports);
        return state;
    }

    // ------------------------------------------------ Profile ------------------------------------------------

    public async Task<IReadOnlyList<SyncProfileEntity>> ListProfilesAsync(CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, config_json, cron, schedule_enabled, created_at, updated_at, last_run_at FROM sync_profiles ORDER BY updated_at DESC";
        var list = new List<SyncProfileEntity>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(ReadProfile(r));
        return list;
    }

    public async Task<SyncProfileEntity?> GetProfileAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, config_json, cron, schedule_enabled, created_at, updated_at, last_run_at FROM sync_profiles WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadProfile(r) : null;
    }

    public async Task<SyncProfileEntity> SaveProfileAsync(SyncProfileEntity e, CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sync_profiles (id, name, config_json, cron, schedule_enabled, created_at, updated_at, last_run_at)
            VALUES (@id, @name, @config, @cron, @enabled, @created, @updated, @last)
            """;
        cmd.Parameters.AddWithValue("@id", e.Id.ToString());
        cmd.Parameters.AddWithValue("@name", e.Name);
        cmd.Parameters.AddWithValue("@config", e.ConfigJson);
        cmd.Parameters.AddWithValue("@cron", (object?)e.Cron ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", e.ScheduleEnabled ? 1L : 0L);
        cmd.Parameters.AddWithValue("@created", e.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updated", e.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@last", (object?)e.LastRunAt?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return e;
    }

    public async Task DeleteProfileAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_profiles WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetLastRunAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = repo.OpenShared();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sync_profiles SET last_run_at = @t WHERE id = @id";
        cmd.Parameters.AddWithValue("@t", at.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static SyncProfileEntity ReadProfile(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        Name = r.GetString(1),
        ConfigJson = r.GetString(2),
        Cron = r.IsDBNull(3) ? null : r.GetString(3),
        ScheduleEnabled = r.GetInt64(4) != 0,
        CreatedAt = DateTimeOffset.Parse(r.GetString(5)),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(6)),
        LastRunAt = r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
    };
}
