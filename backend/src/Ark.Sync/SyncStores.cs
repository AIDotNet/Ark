using Ark.Core.Sync;

namespace Ark.Sync;

/// <summary>任务持久化抽象（由 Api 层实现于 ark.db；进程重启后可恢复/续传）。</summary>
public interface ISyncTaskStore
{
    /// <summary>保存任务全量状态（状态/进度/日志/报告/对比结果 + 计划与请求快照 + 断点）。</summary>
    Task SaveTaskAsync(SyncTaskState state, CancellationToken ct = default);

    /// <summary>读取任务（含 Plan/Request/Checkpoint 等运行所需引用）。</summary>
    Task<StoredTask?> GetTaskAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<StoredTask>> ListTasksAsync(
        TaskKind? kind, SyncTaskStatus? status, Guid? profileId, int limit, int offset, CancellationToken ct = default);

    /// <summary>启动恢复：把遗留的 Running 置为 Interrupted（可续传）。</summary>
    Task MarkStartupInterruptedAsync(CancellationToken ct = default);

    Task<int> CountActiveByProfileAsync(Guid profileId, CancellationToken ct = default);
}

/// <summary>存储中的任务：状态 + 运行所需引用。</summary>
public sealed record StoredTask
{
    public required SyncTaskState State { get; init; }
    public ExecutionPlan? Plan { get; init; }
    public SyncExecuteRequest? Request { get; init; }
    public CompareRequest? CompareReq { get; init; }
    public SyncCheckpoint Checkpoint { get; init; } = SyncCheckpoint.None;
}

/// <summary>同步 Profile（配置存档 + 可选 cron 调度）。</summary>
public sealed record SyncProfileEntity
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>SyncPlanRequest JSON（配置本体；运行时重新生成计划快照）。</summary>
    public required string ConfigJson { get; init; }
    public string? Cron { get; init; }
    public bool ScheduleEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}

public interface ISyncProfileStore
{
    Task<IReadOnlyList<SyncProfileEntity>> ListProfilesAsync(CancellationToken ct = default);
    Task<SyncProfileEntity?> GetProfileAsync(Guid id, CancellationToken ct = default);
    Task<SyncProfileEntity> SaveProfileAsync(SyncProfileEntity entity, CancellationToken ct = default);
    Task DeleteProfileAsync(Guid id, CancellationToken ct = default);
    Task SetLastRunAsync(Guid id, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>调度器回调（由 Api 层实现：解析连接 → 生成计划 → 提交任务）。</summary>
public interface ISyncSchedulerCallbacks
{
    /// <summary>触发一次 Profile 运行；返回 false 表示无法提交（如连接缺失）。</summary>
    Task<bool> TryRunProfileAsync(Guid profileId, string name);

    Task RecordScheduleMissedAsync(Guid profileId, string name);
}
