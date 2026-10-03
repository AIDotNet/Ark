using System.Collections.Concurrent;
using System.Diagnostics;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

/// <summary>
/// 同步/对比任务调度 v2：
/// - 持久化（ISyncTaskStore）：状态/日志/报告/断点落 ark.db，重启后 Running→Interrupted 可续传；
/// - 并发：MaxConcurrent 个任务并行；任务内表按拓扑分层、MaxParallelTables 并行；
/// - 取消：CancellationTokenSource，块/批边界响应；
/// - 快照：源端一致快照会话（方言支持时），保证 Diff/复制读到同一版本。
/// </summary>
public sealed class SyncTaskManager(ISyncTaskStore store, int maxConcurrent = 2)
{
    private readonly ConcurrentDictionary<Guid, RunHandle> _runs = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _persistLocks = new();
    private readonly SemaphoreSlim _gate = new(maxConcurrent, maxConcurrent);
    /// <summary>表级并行分支写 state.Log/Reports 的互斥锁（普通 List 非线程安全，输出顺序不保证）。</summary>
    private readonly object _stateLock = new();

    private SemaphoreSlim PersistLock(Guid id) => _persistLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    private sealed class RunHandle
    {
        public required SyncTaskState State { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
        private long _lastPersistMs;

        public bool TryClaimPersistSlot()
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastPersistMs);
            if (now - last < 2000) return false;
            return Interlocked.CompareExchange(ref _lastPersistMs, now, last) == last;
        }
    }

    // ------------------------------------------------------------------
    // 提交
    // ------------------------------------------------------------------

    public SyncTaskState SubmitSync(
        SyncExecuteRequest request, Guid? profileId,
        ConnectionSpec sourceSpec, ConnectionSpec targetSpec,
        Func<ConnectionSpec, IDbProvider> factory)
    {
        var state = new SyncTaskState
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.Sync,
            ProfileId = profileId,
            Status = SyncTaskStatus.Queued,
            Message = "排队中",
            Request = request,
            Plan = request.Plan,
        };
        state.RowsTotal = request.Plan.Tables
            .Where(t => t.Data is not null)
            .Sum(t => t.Data!.EstimatedRows ?? 0);
        Start(state, sourceSpec, targetSpec, factory, SyncCheckpoint.None);
        return state;
    }

    public SyncTaskState SubmitCompare(
        CompareRequest request,
        ConnectionSpec sourceSpec, ConnectionSpec targetSpec,
        Func<ConnectionSpec, IDbProvider> factory)
    {
        var state = new SyncTaskState
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.Compare,
            Status = SyncTaskStatus.Queued,
            Message = "排队中",
            CompareRequestRef = request,
        };
        Start(state, sourceSpec, targetSpec, factory, SyncCheckpoint.None);
        return state;
    }

    /// <summary>从存储中的任务续传/重试（retry 传 resume=false）。</summary>
    public SyncTaskState SubmitResume(
        StoredTask stored,
        ConnectionSpec sourceSpec, ConnectionSpec targetSpec,
        Func<ConnectionSpec, IDbProvider> factory,
        bool resume = true)
    {
        if (stored.Plan is null || stored.Request is null)
            throw ArkException.Validation("任务缺少计划快照，无法续传");
        var state = new SyncTaskState
        {
            Id = Guid.NewGuid(),
            Kind = TaskKind.Sync,
            ProfileId = stored.State.ProfileId,
            Status = SyncTaskStatus.Queued,
            Message = resume ? "排队中（续传）" : "排队中（重试）",
            Request = stored.Request,
            Plan = stored.Plan,
        };
        var checkpoint = resume ? stored.Checkpoint : SyncCheckpoint.None;
        foreach (var r in stored.State.Reports.Where(r => r.Error is null))
            state.Reports.Add(r);
        if (resume && stored.State.RowsTotal > 0) state.RowsTotal = stored.State.RowsTotal;
        state.Log.Add(resume ? "续传任务（跳过已完成表）" : "重新执行任务");
        Start(state, sourceSpec, targetSpec, factory, checkpoint);
        return state;
    }

    private void Start(
        SyncTaskState state, ConnectionSpec sourceSpec, ConnectionSpec targetSpec,
        Func<ConnectionSpec, IDbProvider> factory, SyncCheckpoint checkpoint)
    {
        state.Checkpoint = checkpoint;
        var cts = new CancellationTokenSource();
        var handle = new RunHandle { State = state, Cts = cts };
        _runs[state.Id] = handle;
        _ = PersistNowAsync(state);
        handle.Task = Task.Run(() => RunAsync(state, checkpoint, sourceSpec, targetSpec, factory, cts.Token));
    }

    // ------------------------------------------------------------------
    // 查询 / 控制
    // ------------------------------------------------------------------

    public SyncTaskState? Get(Guid id) =>
        _runs.TryGetValue(id, out var h) ? h.State : null;

    public async Task<StoredTask?> GetStoredAsync(Guid id, CancellationToken ct = default) =>
        _runs.TryGetValue(id, out var h)
            ? new StoredTask
            {
                State = h.State,
                Plan = h.State.Plan,
                Request = h.State.Request,
                CompareReq = h.State.CompareRequestRef,
                Checkpoint = h.State.Checkpoint,
            }
            : await store.GetTaskAsync(id, ct);

    public void Cancel(Guid id)
    {
        if (!_runs.TryGetValue(id, out var h)) return;
        if (h.State.Status is SyncTaskStatus.Queued or SyncTaskStatus.Running)
        {
            h.State.Status = SyncTaskStatus.CancelRequested;
            h.State.Message = "取消中（等待当前块/批结束）…";
            h.Cts.Cancel();
            _ = PersistNowAsync(h.State);
        }
    }

    public IReadOnlyList<SyncTaskState> RunningStates() =>
        _runs.Values.Select(h => h.State).OrderByDescending(s => s.StartedAt).ToList();

    public Task<IReadOnlyList<StoredTask>> ListAsync(
        TaskKind? kind, SyncTaskStatus? status, Guid? profileId, int limit, int offset, CancellationToken ct = default) =>
        store.ListTasksAsync(kind, status, profileId, limit, offset, ct);

    // ------------------------------------------------------------------
    // 执行
    // ------------------------------------------------------------------

    private async Task RunAsync(
        SyncTaskState state, SyncCheckpoint checkpoint,
        ConnectionSpec sourceSpec, ConnectionSpec targetSpec,
        Func<ConnectionSpec, IDbProvider> factory, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cancelledWhileQueued = false;
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            cancelledWhileQueued = true;
        }

        if (cancelledWhileQueued)
        {
            Finish(state, SyncTaskStatus.Cancelled, "任务在排队时被取消");
            await PersistNowAsync(state);
            _runs.TryRemove(state.Id, out _);
            return;
        }

        try
        {
            state.Status = SyncTaskStatus.Running;
            state.Log.Add(state.Kind == TaskKind.Compare
                ? $"开始对比: {sourceSpec.Dialect} → {targetSpec.Dialect}"
                : $"开始同步: {sourceSpec.Dialect} → {targetSpec.Dialect}");
            await PersistNowAsync(state, ct);

            await using var source = factory(sourceSpec);
            await using var target = factory(targetSpec);

            // 源端一致快照（方言不支持时降级为普通读并记录）
            var snapDb = state.Plan?.Options.SourceDatabase ?? state.CompareRequestRef?.SourceDatabase ?? "";
            ISnapshotSession? snapshot = null;
            SemaphoreSlim? snapshotGate = null;
            try
            {
                snapshot = await source.BeginSnapshotAsync(snapDb, ct);
                // 快照会话是单条物理连接：多表并行时源端读必须互斥（表级并行保留，写目标各自连接不受限）
                snapshotGate = new SemaphoreSlim(1, 1);
                state.Log.Add($"源端快照: {snapshot.Description}");
            }
            catch (Exception ex) when (ex is NotSupportedException or ArkException)
            {
                state.Log.Add($"源端一致快照不可用（{ex.Message}），按普通读继续");
            }

            // PG 目标：自动创建缺失的非 public schema（跨 schema 同步开箱即用）
            if (target.Dialect == ArkDialect.PostgreSQL)
            {
                var schemas = (state.Plan?.Tables ?? []).Select(t => t.Target.Schema)
                    .Concat(state.CompareRequestRef is null ? [] : [state.CompareRequestRef.TargetSchema])
                    .Where(s => !string.IsNullOrEmpty(s) && !string.Equals(s, "public", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var schema in schemas)
                {
                    try
                    {
                        await target.ExecuteDdlAsync(
                            state.Plan?.Options.TargetDatabase ?? state.CompareRequestRef!.TargetDatabase,
                            [$"CREATE SCHEMA IF NOT EXISTS \"{schema}\""], ct);
                    }
                    catch { /* 已存在或无权限：由后续步骤报错 */ }
                }
            }

            try
            {
                if (state.Kind == TaskKind.Sync && state.Plan is not null && state.Request is not null)
                {
                    await RunSyncCoreAsync(state, state.Plan, state.Request, checkpoint, source, target, snapshot, snapshotGate, sw, ct,
                        sourceSpec, targetSpec, factory);
                }
                else if (state.Kind == TaskKind.Compare && state.CompareRequestRef is not null)
                {
                    await RunCompareCoreAsync(state, state.CompareRequestRef, source, target, snapshot, snapshotGate, sw, ct);
                }
                else
                {
                    throw ArkException.Validation("任务缺少执行参数");
                }

                var failed = state.Reports.Count(r => r.Error is not null);
                var status = failed == 0
                    ? SyncTaskStatus.Completed
                    : failed == state.Reports.Count ? SyncTaskStatus.Failed : SyncTaskStatus.PartiallyFailed;
                Finish(state, status,
                    $"完成，耗时 {sw.Elapsed.TotalSeconds:F1}s" + (failed > 0 ? $"，{failed} 张表失败" : ""));
                state.Log.Add(state.Message!);
            }
            finally
            {
                if (snapshot is not null) await snapshot.DisposeAsync();
                snapshotGate?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            Finish(state, SyncTaskStatus.Cancelled, $"任务已取消（{sw.Elapsed.TotalSeconds:F1}s）");
            state.Log.Add(state.Message!);
        }
        catch (Exception ex)
        {
            Finish(state, SyncTaskStatus.Failed, $"任务失败: {ex.Message}");
            state.Log.Add(state.Message!);
        }
        finally
        {
            state.CurrentTable = null;
            await PersistNowAsync(state);
            _runs.TryRemove(state.Id, out _);
            _persistLocks.TryRemove(state.Id, out _);
            _gate.Release();
        }
    }

    private void Finish(SyncTaskState state, SyncTaskStatus status, string message)
    {
        state.Status = status;
        state.Message = message;
        state.FinishedAt = DateTimeOffset.UtcNow;
    }

    // ---------------------------- 同步执行 ----------------------------

    private async Task RunSyncCoreAsync(
        SyncTaskState state, ExecutionPlan plan, SyncExecuteRequest req, SyncCheckpoint checkpoint,
        IDbProvider source, IDbProvider target, ISnapshotSession? snapshot, SemaphoreSlim? snapshotGate,
        Stopwatch sw, CancellationToken ct,
        ConnectionSpec sourceSpec, ConnectionSpec targetSpec, Func<ConnectionSpec, IDbProvider> factory)
    {
        state.Log.Add($"计划包含 {plan.Tables.Count} 张表（快照 {plan.Id:N}）");
        var engine = new DataTransferEngine();

        // 视图依赖表：从分层中剥离，最后串行执行（并行层内也无视视图-表顺序问题）
        var tableExecs = plan.Tables.Where(t => t.SourceMeta?.IsView != true).ToList();
        var viewExecs = plan.Tables.Where(t => t.SourceMeta?.IsView == true).ToList();
        var layers = BuildLayers(tableExecs);

        // Truncate 预清理：FK 子表必须先于父表清空（依赖逆序，复用 Kahn 分层取逆序），
        // 避免 TRUNCATE 父表时被子表引用报 0A000。
        // PG 无会话级 FK 开关：先尝试多表合并 TRUNCATE（同语句可一次清空互引外键的整组表），
        // 失败（如选中表之外还有引用表）再回退逐表逆序清空；
        // MySQL/SQLite 清空前会话级关闭 FK 检查（SET FOREIGN_KEY_CHECKS=0 / PRAGMA foreign_keys=OFF）。
        var truncateDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // BuildLayers 为拓扑分层（父表在前），扁平化后逆序即“先子后父”
        var toTruncate = BuildLayers(tableExecs)
            .SelectMany(l => l)
            .Where(t => t.Data is { } d && d.ConflictMode == ConflictMode.Truncate && t.TargetTableExists)
            .ToList();
        if (toTruncate.Count > 1 && target.Dialect == ArkDialect.PostgreSQL)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tableSql = string.Join(", ", toTruncate.Select(te =>
                    target.Ddl.TableName(new TableRef(te.Target.Database, te.Target.Schema, te.Target.Table))));
                var result = await target.ExecuteDdlAsync(toTruncate[0].Target.Database, [$"TRUNCATE TABLE {tableSql};"], ct);
                if (result.Errors.Count == 0)
                    truncateDone.UnionWith(toTruncate.Select(te => te.Source.Table));
                else
                    state.Log.Add($"合并预清理失败，回退逐表逆序清空: {result.Errors[0]}");
            }
            catch (Exception ex)
            {
                state.Log.Add($"预清理连接失败: {ex.Message}");
            }
        }
        foreach (var te in toTruncate.Where(t => !truncateDone.Contains(t.Source.Table)).Reverse())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ddlConn = await target.OpenConnectionAsync(te.Target.Database, ct);
                await using (ddlConn.ConfigureAwait(false))
                {
                    var errors = new List<string>();
                    var stmts = FkCheckStmts(target.Dialect, off: true)
                        .Concat(target.Ddl.TruncateTable(new TableRef(te.Target.Database, te.Target.Schema, te.Target.Table)))
                        .Concat(FkCheckStmts(target.Dialect, off: false));
                    foreach (var stmt in stmts)
                    {
                        await using var cmd = ddlConn.CreateCommand();
                        cmd.CommandText = stmt;
                        try { await cmd.ExecuteNonQueryAsync(ct); }
                        catch (Exception ex) { errors.Add(ex.Message); }
                    }
                    if (errors.Count == 0) truncateDone.Add(te.Source.Table);
                    else state.Log.Add($"[{te.Target.Table}] 预清理失败: {errors[0]}");
                }
            }
            catch (Exception ex)
            {
                state.Log.Add($"[{te.Target.Table}] 预清理连接失败: {ex.Message}");
            }
        }
        var completedBefore = new HashSet<string>(
            state.Reports.Select(r => r.Table), StringComparer.OrdinalIgnoreCase);
        // 续传断点：从断点表开始执行（其前的表已通过 Reports 继承）
        var skipUntilPassed = checkpoint.Table is not null;

        foreach (var layer in layers)
        {
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, plan.Options.MaxParallelTables),
                CancellationToken = ct,
            };
            await Parallel.ForEachAsync(layer, parallel, async (te, ict) =>
            {
                if (completedBefore.Contains(te.Source.Table)) return;
                var isCheckpointTable = skipUntilPassed &&
                                        string.Equals(te.Source.Table, checkpoint.Table, StringComparison.OrdinalIgnoreCase);
                if (skipUntilPassed && !isCheckpointTable) return;
                if (isCheckpointTable) skipUntilPassed = false;

                // 每张表独立的 Provider 实例：避免并行分支共享缓存的 DbConnection
                await using var tSrc = factory(sourceSpec);
                await using var tTgt = factory(targetSpec);
                await ExecuteTableAsync(state, req, te, tSrc, tTgt, engine, snapshot, snapshotGate,
                    isCheckpointTable ? checkpoint.LastKey : null,
                    checkpoint.CompletedActionIds,
                    truncateDone.Contains(te.Source.Table),
                    ict);
                state.Percent = Math.Round(ReportCount(state) * 100.0 / Math.Max(1, plan.Tables.Count), 1);
                await PersistNowAsync(state, ict);
            });
        }

        foreach (var view in viewExecs)
        {
            ct.ThrowIfCancellationRequested();
            await using var vSrc = factory(sourceSpec);
            await using var vTgt = factory(targetSpec);
            await ExecuteTableAsync(state, req, view, vSrc, vTgt, engine, snapshot, snapshotGate, null,
                checkpoint.CompletedActionIds, false, ct);
            state.Percent = Math.Round(ReportCount(state) * 100.0 / Math.Max(1, plan.Tables.Count), 1);
            await PersistNowAsync(state, ct);
        }
    }

    /// <summary>外键依赖分层（层内可并行）。</summary>
    internal static List<List<TableExecution>> BuildLayers(IReadOnlyList<TableExecution> tables)
    {
        var result = new List<List<TableExecution>>();
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<TableExecution>(tables);
        while (remaining.Count > 0)
        {
            var layer = remaining
                .Where(t => Dependencies(t).All(d => done.Contains(d)))
                .ToList();
            if (layer.Count == 0)
            {
                result.Add(remaining.ToList()); // 循环依赖：剩余一层放行
                break;
            }
            result.Add(layer);
            foreach (var t in layer)
            {
                done.Add(t.Source.Table);
                remaining.Remove(t);
            }
        }
        return result;
    }

    private static IReadOnlyList<string> Dependencies(TableExecution te) =>
        te.SourceMeta?.ForeignKeys
            .Select(f => f.ReferencedTable)
            .Where(r => !string.Equals(r, te.Source.Table, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

    /// <summary>清空目标表前后的会话级 FK 检查开关（PG 无会话开关返回空：靠先子后父的逆序清空避免引用冲突）。</summary>
    private static string[] FkCheckStmts(ArkDialect dialect, bool off) => (dialect, off) switch
    {
        (ArkDialect.MySQL, true) => ["SET FOREIGN_KEY_CHECKS=0"],
        (ArkDialect.MySQL, false) => ["SET FOREIGN_KEY_CHECKS=1"],
        (ArkDialect.SQLite, true) => ["PRAGMA foreign_keys=OFF"],
        (ArkDialect.SQLite, false) => ["PRAGMA foreign_keys=ON"],
        _ => [],
    };

    private async Task ExecuteTableAsync(
        SyncTaskState state, SyncExecuteRequest req, TableExecution te,
        IDbProvider source, IDbProvider target, DataTransferEngine engine, ISnapshotSession? snapshot,
        SemaphoreSlim? snapshotGate,
        string? resumeKey, IReadOnlyList<string> completedActionIds, bool truncateAlreadyDone,
        CancellationToken ct)
    {
        var table = te.Target.Table;
        state.CurrentTable = table;
        state.Message = $"处理 {table}";
        long ins = 0, upd = 0, del = 0;
        var ddlOk = 0;
        var ddlFail = 0;
        var warnings = new List<string>();
        string? error = null;
        VerifyResult? verification = null;
        var tableSw = Stopwatch.StartNew();

        try
        {
            var srcRef = new TableRef(req.Plan.Options.SourceDatabase, te.Source.Schema, te.Source.Table);
            var tgtRef = new TableRef(te.Target.Database, te.Target.Schema, te.Target.Table);
            var srcMeta = te.SourceMeta
                          ?? await source.GetTableAsync(req.Plan.Options.SourceDatabase, te.Source.Schema, te.Source.Table, ct);
            var tgtConverted = te.TargetConverted
                               ?? TableConverter.Convert(srcMeta, source.Dialect, target.Dialect).Converted;

            // 1) 结构动作
            foreach (var action in te.StructureActions)
            {
                ct.ThrowIfCancellationRequested();
                if (req.SkipActionIds.Contains(action.Id) || completedActionIds.Contains(action.Id))
                {
                    AddLog(state, $"[{table}] 跳过动作: {action.Summary}");
                    continue;
                }
                if (action.IsDestructive && !req.ConfirmDestructive)
                {
                    warnings.Add($"破坏性动作未确认，已跳过: {action.Summary}");
                    AddLog(state, $"[{table}] {warnings[^1]}");
                    continue;
                }
                var (_, errors) = await target.ExecuteDdlAsync(te.Target.Database, [action.Sql], ct);
                if (errors.Count == 0)
                {
                    ddlOk++;
                    AddLog(state, $"[{table}] ✓ {action.Summary}");
                }
                else
                {
                    ddlFail++;
                    error ??= errors[0];
                    AddLog(state, $"[{table}] ✗ {action.Summary}: {errors[0]}");
                }
                foreach (var w in action.Warnings) warnings.Add(w);
            }

            // 2) 数据同步
            if (error is null && te.Data is { } data)
            {
                if (srcMeta.IsView)
                {
                    AddLog(state, $"[{table}] 视图不同步数据");
                }
                else if (data.EffectiveMethod == DataSyncMethod.RowDiff)
                {
                    var stats = await engine.DiffTableAsync(source, target, srcRef, srcMeta, tgtRef, tgtConverted,
                        te.Source, req.Plan.Options, snapshot,
                        (i, u, d, lastKey) =>
                        {
                            (ins, upd, del) = (i, u, d);
                            state.RowsDone = SumReports(state, r => r.RowsInserted + r.RowsUpdated + r.RowsDeleted) + i + u + d;
                            state.Checkpoint = state.Checkpoint with { Table = table, LastKey = lastKey, RowsDone = state.RowsDone };
                            MarkDirty(state);
                        },
                        resumeKey,
                        apply: true, ct: ct, snapshotGate: snapshotGate);
                    (ins, upd, del) = (stats.Inserted, stats.Updated, stats.Deleted);
                    warnings.AddRange(stats.Warnings);
                    AddLog(state, $"[{table}] Diff 完成: +{stats.Inserted} ~{stats.Updated} -{stats.Deleted}" +
                            $"（{stats.Windows} 窗口 / {stats.DiffWindows} 差异）");
                }
                else
                {
                    var stats = await engine.FullCopyAsync(source, target, srcRef, srcMeta, tgtRef, tgtConverted,
                        te.Source, req.Plan.Options,
                        snapshot,
                        n =>
                        {
                            ins = n;
                            state.RowsDone = SumReports(state, r => r.RowsInserted) + n;
                            state.Checkpoint = state.Checkpoint with { Table = table, RowsDone = state.RowsDone };
                            MarkDirty(state);
                        },
                        resumeKey,
                        skipTruncate: truncateAlreadyDone,
                        ct: ct, snapshotGate: snapshotGate);
                    ins = stats.Inserted;
                    warnings.AddRange(stats.Warnings);
                    AddLog(state, $"[{table}] 全量复制 {stats.Inserted} 行（通道 {stats.Channel ?? "INSERT"}）");
                }

                // 3) 校验
                verification = await SyncValidator.VerifyAsync(
                    source, target, srcRef, srcMeta, tgtRef, tgtConverted,
                    DataTransferEngine.BuildColumnPairs(srcMeta, tgtConverted, te.Source, warnings),
                    req.Plan.Options.VerifyMode, te.Source.Where,
                    req.Plan.Options.ChunkRows, snapshot,
                    DataTransferEngine.BuildMask(te.Source,
                        DataTransferEngine.BuildColumnPairs(srcMeta, tgtConverted, te.Source, warnings)
                            .Select(p => p.Tgt).ToList(), tgtConverted),
                    ct, snapshotGate: snapshotGate);
                if (verification is { } v)
                    AddLog(state, v.Match
                        ? $"[{table}] 校验通过：行数 {v.SourceRows} = {v.TargetRows}" + (v.SampleMatch is null ? "" : "，抽样一致")
                        : $"[{table}] 校验失败：行数 源 {v.SourceRows} ≠ 目标 {v.TargetRows}");
            }

            // 4) 延后的索引/外键（数据完成后）
            if (error is null)
            {
                foreach (var action in te.PostCopyActions)
                {
                    ct.ThrowIfCancellationRequested();
                    if (req.SkipActionIds.Contains(action.Id) || completedActionIds.Contains(action.Id)) continue;
                    var (_, errors) = await target.ExecuteDdlAsync(te.Target.Database, [action.Sql], ct);
                    if (errors.Count == 0)
                    {
                        ddlOk++;
                        AddLog(state, $"[{table}] ✓ {action.Summary}");
                    }
                    else
                    {
                        ddlFail++;
                        error ??= errors[0];
                        AddLog(state, $"[{table}] ✗ {action.Summary}: {errors[0]}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error ??= ex.Message;
            AddLog(state, $"[{table}] 表级失败: {ex.Message}");
        }

        AddReport(state, new TableSyncReport(
            table, ddlOk, ddlFail, ins, upd, del,
            Math.Round(tableSw.Elapsed.TotalMilliseconds, 1), warnings, error, verification));
        state.Checkpoint = SyncCheckpoint.None; // 本表完成，清除断点
    }

    // ---------------------------- 对比执行 ----------------------------

    private async Task RunCompareCoreAsync(
        SyncTaskState state, CompareRequest req,
        IDbProvider source, IDbProvider target, ISnapshotSession? snapshot, SemaphoreSlim? snapshotGate,
        Stopwatch sw, CancellationToken ct)
    {
        var engine = new DataTransferEngine();
        var results = new List<TableCompareResult>();
        var keysByTable = new Dictionary<string, DiffKeys>(StringComparer.OrdinalIgnoreCase);
        var options = new SyncPlanRequest
        {
            SourceConnectionId = req.SourceConnectionId,
            SourceDatabase = req.SourceDatabase,
            TargetConnectionId = req.TargetConnectionId,
            TargetDatabase = req.TargetDatabase,
            ChunkRows = req.ChunkRows,
            Tables = req.Tables,
        };

        var total = Math.Max(1, req.Tables.Count);
        var idx = 0;
        foreach (var sel in req.Tables)
        {
            ct.ThrowIfCancellationRequested();
            idx++;
            state.CurrentTable = sel.Table;
            state.Message = $"对比 {sel.Table}（{idx}/{total}）";
            var srcRef = new TableRef(req.SourceDatabase, sel.Schema, sel.Table);
            var tgtSchema = !string.IsNullOrWhiteSpace(req.TargetSchema)
                ? req.TargetSchema
                : target.Dialect == ArkDialect.PostgreSQL
                    ? (string.IsNullOrEmpty(sel.Schema) ? "public" : sel.Schema)
                    : null;
            var tgtRef = new TableRef(req.TargetDatabase, tgtSchema, sel.Table);

            TableCompareResult result;
            DiffKeys keys = DiffKeys.Empty;
            try
            {
                var srcMeta = await source.GetTableAsync(req.SourceDatabase, sel.Schema, sel.Table, ct);
                CanonicalTable? existing = null;
                try { existing = await target.GetTableAsync(req.TargetDatabase, tgtSchema, sel.Table, ct); }
                catch (ArkException) { }

                if (srcMeta.IsView)
                {
                    var srcDef = await source.GetViewDefinitionAsync(req.SourceDatabase, sel.Schema, sel.Table, ct);
                    var tgtDef = existing is not null
                        ? await target.GetViewDefinitionAsync(req.TargetDatabase, tgtSchema, sel.Table, ct)
                        : null;
                    var changed = existing is null || !string.Equals(
                        NormalizedDef(srcDef), NormalizedDef(tgtDef), StringComparison.OrdinalIgnoreCase);
                    result = new TableCompareResult
                    {
                        Table = sel.Table,
                        StructureChanges = changed ? 1 : 0,
                        Error = existing is null ? "目标视图不存在" : null,
                    };
                }
                else if (existing is null)
                {
                    long srcCount;
                    await using (var c = await source.OpenConnectionAsync(req.SourceDatabase, ct))
                        srcCount = await SyncValidator.CountRowsAsync(source, c, srcRef, sel.Where, ct);
                    result = new TableCompareResult
                    {
                        Table = sel.Table,
                        StructureChanges = 1,
                        Inserted = srcCount,
                        Error = "目标表不存在",
                    };
                }
                else if (srcMeta.PrimaryKeyColumns.Count == 0)
                {
                    result = new TableCompareResult
                    {
                        Table = sel.Table,
                        Error = "该表没有主键，无法做行级对比",
                    };
                }
                else
                {
                    var (converted, _) = TableConverter.Convert(srcMeta, source.Dialect, target.Dialect);
                    var detail = StructureDiffer.DiffDetailed(existing, converted, tgtRef);
                    var structureChanges = detail.Diff.AddedColumns.Count + detail.Diff.AlteredColumns.Count
                                           + detail.Diff.DroppedColumns.Count + detail.Diff.AddedIndexes.Count
                                           + detail.Diff.DroppedIndexes.Count + detail.Diff.AddedForeignKeys.Count
                                           + detail.Diff.DroppedForeignKeys.Count + (detail.Diff.PrimaryKeyChanged ? 1 : 0);

                    var stats = await engine.DiffTableAsync(
                        source, target, srcRef, srcMeta, tgtRef, converted,
                        sel, options,
                        snapshot,
                        (_, _, _, _) => MarkDirty(state),
                        apply: false,
                        maxDiffKeys: req.MaxDiffKeys,
                        maxSamples: 20,
                        ct: ct);
                    keys = stats.Keys;

                    long realSrc, tgtCount;
                    await using (var c = await source.OpenConnectionAsync(req.SourceDatabase, ct))
                        realSrc = await SyncValidator.CountRowsAsync(source, c, srcRef, sel.Where, ct);
                    await using (var c = await target.OpenConnectionAsync(req.TargetDatabase, ct))
                        tgtCount = await SyncValidator.CountRowsAsync(target, c, tgtRef, null, ct);

                    result = new TableCompareResult
                    {
                        Table = sel.Table,
                        Chunks = stats.Windows,
                        DiffChunks = stats.DiffWindows,
                        Inserted = stats.Inserted,
                        Updated = stats.Updated,
                        Deleted = stats.Deleted,
                        StructureChanges = structureChanges,
                        Samples = stats.Samples,
                        SamplesTruncated = stats.Keys.InsertedTruncated || stats.Keys.UpdatedTruncated || stats.Keys.DeletedTruncated,
                        Verification = new VerifyResult(realSrc, tgtCount, realSrc == tgtCount, null),
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new TableCompareResult { Table = sel.Table, Error = ex.Message };
            }

            results.Add(result);
            keysByTable[sel.Table] = keys;
            state.Reports.Add(new TableSyncReport(sel.Table, 0, 0, result.Inserted, result.Updated, result.Deleted,
                0, [], result.Error));
            state.Percent = Math.Round(results.Count * 100.0 / total, 1);
            await PersistNowAsync(state, ct);
        }

        state.Compare = new CompareResult
        {
            Tables = results,
            DiffKeysByTable = keysByTable,
        };
        state.Log.Add($"对比完成，耗时 {sw.Elapsed.TotalSeconds:F1}s");
    }

    private static string? NormalizedDef(string? def) => def?.Trim().Replace("\r", "").Replace("\n", " ");

    // ---------------------------- 并行安全的状态写入 ----------------------------
    // 表级并行分支共用同一份 state：Log/Reports 是普通 List，写入必须互斥；读取方按快照容忍顺序不严格

    private void AddLog(SyncTaskState state, string message)
    {
        lock (_stateLock) state.Log.Add(message);
    }

    private void AddReport(SyncTaskState state, TableSyncReport report)
    {
        lock (_stateLock) state.Reports.Add(report);
    }

    private long SumReports(SyncTaskState state, Func<TableSyncReport, long> selector)
    {
        lock (_stateLock) return state.Reports.Sum(selector);
    }

    private int ReportCount(SyncTaskState state)
    {
        lock (_stateLock) return state.Reports.Count;
    }

    // ---------------------------- 持久化（节流） ----------------------------

    private void MarkDirty(SyncTaskState state)
    {
        if (!_runs.TryGetValue(state.Id, out var h)) return;
        if (!h.TryClaimPersistSlot()) return;
        _ = Task.Run(async () =>
        {
            var gate = PersistLock(state.Id);
            await gate.WaitAsync();
            try
            {
                // 终态由 finally 落库；此处丢弃过期写入，避免旧快照覆盖带日志的最终状态
                if (state.FinishedAt is not null) return;
                await store.SaveTaskAsync(state);
            }
            finally { gate.Release(); }
        });
    }

    private async Task PersistNowAsync(SyncTaskState state, CancellationToken ct = default)
    {
        var gate = PersistLock(state.Id);
        await gate.WaitAsync(ct);
        try
        {
            await store.SaveTaskAsync(state, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 持久化失败不影响任务状态 */ }
        finally { gate.Release(); }
    }
}
