using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Ark.Core.Metadata;

namespace Ark.Core.Sync;

public enum SyncMode
{
    StructureOnly,
    StructureAndData,
}

public enum DataSyncMethod
{
    /// <summary>全量分批复制（冲突语义由 ConflictMode 决定）。</summary>
    FullCopy,
    /// <summary>行级 Diff：按主键对比，仅同步 新增/变更/删除 的行。</summary>
    RowDiff,
}

/// <summary>全量复制的冲突语义。</summary>
public enum ConflictMode
{
    /// <summary>目标已有数据时直接 INSERT（冲突报错，表级失败）。</summary>
    Error,
    /// <summary>复制前清空目标表（TRUNCATE/DELETE）。</summary>
    Truncate,
    /// <summary>跳过已存在的主键（PG/SQLite: ON CONFLICT DO NOTHING；MySQL: INSERT IGNORE）。要求主键。</summary>
    SkipExisting,
}

/// <summary>同步后校验模式。</summary>
public enum VerifyMode
{
    /// <summary>仅统计报告，不做校验。</summary>
    Off,
    /// <summary>行数对比 + 抽样块哈希复检（默认）。</summary>
    Sample,
    /// <summary>行数对比 + 全量分块哈希复检（仅对比任务使用）。</summary>
    Full,
}

public enum MaskRuleKind
{
    Null,
    RandomInt,
    RandomLetter,
    RandomDate,
    Fixed,
    SqlExpr,
}

/// <summary>脱敏规则：ColumnPattern 支持 * 通配（如 email / user_* / *）。</summary>
public sealed record MaskRule(string ColumnPattern, MaskRuleKind Kind, string? Value = null);

public enum ChunkStatus { Matched, Different, MissingInTarget, MissingInSource }

public sealed record TableSelection
{
    [Required] public string Database { get; init; } = "";
    public string? Schema { get; init; }
    [Required] public string Table { get; init; } = "";
    /// <summary>源端行过滤 WHERE 片段（不含 WHERE 关键字；计划期试编译校验）。</summary>
    public string? Where { get; init; }
    /// <summary>源列 → 目标列映射（缺省同名；改名确认后自动带上）。</summary>
    public IReadOnlyDictionary<string, string>? ColumnMap { get; init; }
    /// <summary>表级数据方式覆盖（缺省用全局 DataMethod）。</summary>
    public DataSyncMethod? MethodOverride { get; init; }
    /// <summary>不同步的列（源端列名）。</summary>
    public IReadOnlyList<string>? ExcludeColumns { get; init; }
    /// <summary>写目标前应用的脱敏规则。</summary>
    public IReadOnlyList<MaskRule>? MaskRules { get; init; }
}

public sealed record SyncPlanRequest
{
    [Required] public Guid SourceConnectionId { get; init; }
    [Required] public string SourceDatabase { get; init; } = "";
    [Required] public Guid TargetConnectionId { get; init; }
    [Required] public string TargetDatabase { get; init; } = "";
    public SyncMode Mode { get; init; } = SyncMode.StructureAndData;
    public DataSyncMethod DataMethod { get; init; } = DataSyncMethod.FullCopy;
    /// <summary>全量复制冲突语义（取代 v1 的 TruncateBeforeCopy）。</summary>
    public ConflictMode ConflictMode { get; init; } = ConflictMode.Error;
    /// <summary>删除目标端多余的表（DropTable）。</summary>
    public bool DropExtraTables { get; init; }
    /// <summary>Diff 模式下删除目标端多余的行。</summary>
    public bool DeleteExtraRows { get; init; }
    public bool AlignAutoIncrement { get; init; } = true;
    /// <summary>同步的批次行数。</summary>
    [Range(100, 100_000)] public int BatchSize { get; init; } = 1000;
    /// <summary>Diff 分块行数（每块目标行数，keyset 边界）。</summary>
    [Range(50, 100_000)] public int ChunkRows { get; init; } = 2000;
    /// <summary>大表导入：二级索引/外键延后到数据复制完成后创建。</summary>
    public bool DeferIndexes { get; init; }
    public VerifyMode VerifyMode { get; init; } = VerifyMode.Sample;
    /// <summary>表级并行度（拓扑分层内并行）。</summary>
    [Range(1, 8)] public int MaxParallelTables { get; init; } = 2;
    /// <summary>目标端 schema 覆盖（PG 目标默认 public；留空用方言启发式）。</summary>
    public string? TargetSchema { get; init; }
    [MinLength(1)] public required IReadOnlyList<TableSelection> Tables { get; init; }
}

public sealed record ConversionIssue(string Severity, string Message);

public sealed record DdlAction
{
    /// <summary>稳定 ID，供前端勾选/取消。</summary>
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public required string Sql { get; init; }
    public bool IsDestructive { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record ExtensionDependency
{
    public required string Name { get; init; }
    public required string InstallSql { get; init; }
    public bool Present { get; init; }
    public string? Note { get; init; }
}

/// <summary>列改名候选：删除列 X + 新增列 Y 满足启发式条件（位置相邻、类型一致），由用户确认是否视为改名。</summary>
public sealed record RenameCandidate(string DropColumn, string AddColumn, string Confidence);

public sealed record DataPlan
{
    public required DataSyncMethod Method { get; init; }
    /// <summary>经表级覆盖与无主键回退后的实际执行方式。</summary>
    public DataSyncMethod EffectiveMethod { get; init; }
    public bool HasPrimaryKey { get; init; }
    public long? EstimatedRows { get; init; }
    public ConflictMode ConflictMode { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 表执行单元：动作 SQL 全量快照 + 源/目标元数据（执行阶段零规划，避免 TOCTOU）。
/// </summary>
public sealed record TableExecution
{
    public required TableSelection Source { get; init; }
    public required TableSelection Target { get; init; }
    public bool TargetTableExists { get; init; }
    /// <summary>数据复制前执行的结构动作（建表/改表/建索引/外键…）。</summary>
    public IReadOnlyList<DdlAction> StructureActions { get; init; } = [];
    /// <summary>DeferIndexes 时延后到数据复制完成后的动作（二级索引/外键）。</summary>
    public IReadOnlyList<DdlAction> PostCopyActions { get; init; } = [];
    public DataPlan? Data { get; init; }
    public IReadOnlyList<ConversionIssue> ConversionIssues { get; init; } = [];
    public IReadOnlyList<ExtensionDependency> Extensions { get; init; } = [];
    public IReadOnlyList<RenameCandidate> RenameCandidates { get; init; } = [];
    /// <summary>源表元数据快照（视图/无主键判定、Diff 列集）。</summary>
    public CanonicalTable? SourceMeta { get; init; }
    /// <summary>转换后的目标规范模型（类型映射/函数翻译已应用）。</summary>
    public CanonicalTable? TargetConverted { get; init; }
}

/// <summary>可持久化的执行计划快照：预览所见即执行所得。</summary>
public sealed record ExecutionPlan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required SyncPlanRequest Options { get; init; }
    public required IReadOnlyList<TableExecution> Tables { get; init; }
}

/// <summary>同步后校验结果。</summary>
public sealed record VerifyResult(long SourceRows, long TargetRows, bool CountsMatch, bool? SampleMatch)
{
    public bool Match => CountsMatch && (SampleMatch ?? true);
}

public sealed record TableSyncReport(
    string Table,
    int DdlExecuted,
    int DdlFailed,
    long RowsInserted,
    long RowsUpdated,
    long RowsDeleted,
    double ElapsedMs,
    IReadOnlyList<string> Warnings,
    string? Error,
    VerifyResult? Verification = null);

public enum SyncTaskStatus { Queued, Running, CancelRequested, Cancelled, Completed, Failed, PartiallyFailed, Interrupted }

public enum TaskKind { Sync, Compare }

/// <summary>断点：续传时从该位置继续（键集游标 + 已完成的动作 ID）。</summary>
public sealed record SyncCheckpoint(string? Table, string? LastKey, long RowsDone, IReadOnlyList<string> CompletedActionIds)
{
    public static readonly SyncCheckpoint None = new(null, null, 0, []);
}

// ---------------------------------------------------------------------------
// 对比（先比后改）
// ---------------------------------------------------------------------------

public sealed record CompareRequest
{
    [Required] public Guid SourceConnectionId { get; init; }
    [Required] public string SourceDatabase { get; init; } = "";
    [Required] public Guid TargetConnectionId { get; init; }
    [Required] public string TargetDatabase { get; init; } = "";
    [MinLength(1)] public required IReadOnlyList<TableSelection> Tables { get; init; }
    [Range(50, 100_000)] public int ChunkRows { get; init; } = 2000;
    /// <summary>目标端 schema 覆盖（PG 目标默认 public）。</summary>
    public string? TargetSchema { get; init; }
    /// <summary>每表每类差异最多保留的差异键数（超出标记截断）。</summary>
    [Range(100, 100_000)] public int MaxDiffKeys { get; init; } = 2000;
}

/// <summary>差异样本行（前 N 条，供前端预览）。</summary>
public sealed record DiffSampleRow(string Key, string ChangeType, string[] SourceValues, string[] TargetValues);

/// <summary>单表对比结果。</summary>
public sealed record TableCompareResult
{
    public required string Table { get; init; }
    public long Chunks { get; init; }
    public long DiffChunks { get; init; }
    public long Inserted { get; init; }
    public long Updated { get; init; }
    public long Deleted { get; init; }
    public int StructureChanges { get; init; }
    public IReadOnlyList<DiffSampleRow> Samples { get; init; } = [];
    public bool SamplesTruncated { get; init; }
    public VerifyResult? Verification { get; init; }
    public string? Error { get; init; }
}

/// <summary>差异键分页存储：按类型分列，超出上限标记截断。</summary>
public sealed record DiffKeys(
    IReadOnlyList<string> Inserted, bool InsertedTruncated,
    IReadOnlyList<string> Updated, bool UpdatedTruncated,
    IReadOnlyList<string> Deleted, bool DeletedTruncated)
{
    public static readonly DiffKeys Empty = new([], false, [], false, [], false);
}

public sealed record TableDiffDetail
{
    public required string Table { get; init; }
    public required TableCompareResult Summary { get; init; }
    public required DiffKeys Keys { get; init; }
}

/// <summary>对比任务结果（持久化于 sync_tasks.result_json）。</summary>
public sealed record CompareResult
{
    public required IReadOnlyList<TableCompareResult> Tables { get; init; }
    /// <summary>差异键明细按表分开持久化（可能较大）。</summary>
    public IReadOnlyDictionary<string, DiffKeys>? DiffKeysByTable { get; init; }
}

public sealed record SyncTaskState
{
    public required Guid Id { get; init; }
    public TaskKind Kind { get; set; } = TaskKind.Sync;
    public Guid? ProfileId { get; set; }
    public SyncTaskStatus Status { get; set; }
    public string? CurrentTable { get; set; }
    public double Percent { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    /// <summary>进度明细（行级计数）。</summary>
    public long RowsDone { get; set; }
    public long RowsTotal { get; set; }
    public List<string> Log { get; } = [];
    public List<TableSyncReport> Reports { get; } = [];
    public CompareResult? Compare { get; set; }

    [JsonIgnore]
    public ExecutionPlan? Plan { get; set; }

    [JsonIgnore]
    public SyncCheckpoint Checkpoint { get; set; } = SyncCheckpoint.None;

    [JsonIgnore]
    public SyncExecuteRequest? Request { get; set; }

    [JsonIgnore]
    public CompareRequest? CompareRequestRef { get; set; }

    public object ToDto() => new
    {
        id = Id,
        kind = Kind.ToString(),
        profileId = ProfileId,
        status = Status.ToString(),
        currentTable = CurrentTable,
        percent = Percent,
        message = Message,
        rowsDone = RowsDone,
        rowsTotal = RowsTotal,
        startedAt = StartedAt,
        finishedAt = FinishedAt,
        error = Error,
        log = Log.ToArray(),
        reports = Reports.ToArray(),
        compare = Compare,
    };
}

public sealed record SyncExecuteRequest
{
    /// <summary>预览阶段生成的计划快照（执行零规划）。</summary>
    [Required] public required ExecutionPlan Plan { get; init; }
    /// <summary>用户在计划预览中取消勾选的动作。</summary>
    public IReadOnlyList<string> SkipActionIds { get; init; } = [];
    /// <summary>确认执行破坏性动作（DROP 等）。</summary>
    public bool ConfirmDestructive { get; init; }
}
