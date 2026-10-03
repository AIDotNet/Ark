using System.Data.Common;
using System.Threading.Channels;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

public sealed record TableStats(
    long Inserted, long Updated, long Deleted, List<string> Warnings,
    string? Channel = null, VerifyResult? Verification = null);

/// <summary>
/// 数据传输引擎 v2：
/// - FullCopy：冲突语义（清空/跳过已存在/报错）+ 原生批量通道（COPY/LOAD DATA/批量绑定，降级多行 INSERT）
///   + 读写流水线（Channel，读 N+1 批与写 N 批并发）+ 脱敏/列映射/WHERE 过滤
/// - RowDiff：委托 ChunkedDiffEngine（keyset 窗口流式 merge-join）
/// </summary>
public sealed class DataTransferEngine
{
    /// <summary>构建列掩码（写路径脱敏 + 哈希对齐）。</summary>
    public static Dictionary<int, Func<Random, object?, object?>> BuildMask(
        TableSelection selection, IReadOnlyList<string> tgtColNames, CanonicalTable tgtConverted) =>
        Masking.Compile(selection.MaskRules, tgtColNames, tgtConverted.Columns);

    /// <summary>构建读写列对：排除生成列/ExcludeColumns，应用 ColumnMap。</summary>
    public static IReadOnlyList<(string Src, string Tgt)> BuildColumnPairs(
        CanonicalTable srcMeta, CanonicalTable tgtConverted, TableSelection sel, List<string> warnings)
    {
        var tgtNames = tgtConverted.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = (sel.ExcludeColumns ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(string, string)>();
        foreach (var col in srcMeta.Columns)
        {
            if (excluded.Contains(col.Name)) continue;
            var tgt = sel.ColumnMap is not null && sel.ColumnMap.TryGetValue(col.Name, out var mapped) ? mapped : col.Name;
            if (!tgtNames.Contains(tgt))
            {
                warnings.Add($"列 {col.Name} 在目标端无对应列（映射 {tgt}），已跳过");
                continue;
            }
            Identifier.EnsureValid(tgt, "映射列名");
            pairs.Add((col.Name, tgt));
        }
        return pairs;
    }

    // ------------------------------------------------------------------
    // 全量复制
    // ------------------------------------------------------------------

    public async Task<TableStats> FullCopyAsync(
        IDbProvider source, IDbProvider target,
        TableRef srcRef, CanonicalTable srcMeta,
        TableRef tgtRef, CanonicalTable tgtConverted,
        TableSelection selection, SyncPlanRequest options,
        ISnapshotSession? snapshot = null,
        Action<long>? onRows = null,
        string? resumeKey = null,
        bool skipTruncate = false,
        CancellationToken ct = default,
        SemaphoreSlim? snapshotGate = null)
    {
        var warnings = new List<string>();
        var colPairs = BuildColumnPairs(srcMeta, tgtConverted, selection, warnings);
        var srcCols = colPairs.Select(p => p.Src).ToList();
        var tgtCols = colPairs.Select(p => p.Tgt).ToList();
        var writeableCols = tgtConverted.Columns.Where(c => !c.IsGenerated).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var insertCols = tgtCols.Where(writeableCols.Contains).ToList();
        var conflict = tgtConverted.PrimaryKeyColumns.Count == 0 && options.ConflictMode == ConflictMode.SkipExisting
            ? ConflictMode.Error
            : options.ConflictMode;

        if (conflict == ConflictMode.Truncate && !skipTruncate)
        {
            var errors = await target.ExecuteDdlAsync(tgtRef.Database, target.Ddl.TruncateTable(tgtRef), ct);
            if (errors.Errors.Count > 0)
                throw new ArkException(ArkErrorCodes.SyncExecuteFailed, $"清空目标表失败: {errors.Errors[0]}");
        }

        await using var srcConn = snapshot is not null ? null : await source.OpenConnectionAsync(srcRef.Database, ct);
        var srcRead = snapshot?.Connection ?? srcConn!;
        await using var tgtConn = await target.OpenConnectionAsync(tgtRef.Database, ct);
        await ApplyPragmasAsync(target, tgtConn, on: true);

        var maskRules = selection.MaskRules;
        var maskByIndex = Masking.Compile(maskRules, insertCols, tgtConverted.Columns);
        var sqlExprCols = Masking.SqlExprColumns(maskRules, insertCols);
        var rnd = new Random(unchecked((int)DateTimeOffset.UtcNow.Ticks));

        // 批量通道：无 SqlExpr 脱敏且非 SkipExisting 且方言支持 → COPY / LOAD DATA；否则降级批量 INSERT
        IBulkWriter? writer = null;
        // 仅 SkipExisting 需要冲突子句；Truncate 与批量通道完全兼容
        var forceBatched = conflict == ConflictMode.SkipExisting || sqlExprCols.Count > 0 ||
                           !target.Capabilities.SupportsBulkCopy;
        string? channelName = null;
        if (!forceBatched)
        {
            try
            {
                writer = await target.OpenBulkWriterAsync(tgtRef, insertCols, tgtConverted.Columns, ct);
                channelName = writer.Channel;
                warnings.Add($"数据通道: {writer.Channel}");
            }
            catch (Exception ex)
            {
                warnings.Add($"批量通道不可用（{ex.Message}），降级为批量 INSERT");
                writer = null;
            }
        }
        else
        {
            channelName = conflict == ConflictMode.SkipExisting ? "INSERT ON CONFLICT" : "批量 INSERT";
            warnings.Add($"数据通道: {channelName}");
        }

        long inserted = 0;
        var channel = Channel.CreateBounded<List<object?[]>>(new BoundedChannelOptions(2)
        {
            SingleWriter = true,
            SingleReader = true,
        });
        var after = resumeKey;

        var producer = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    // 快照会话是单条物理连接：多表并行时源端读必须互斥，避免连接被并发占用
                    if (snapshotGate is not null) await snapshotGate.WaitAsync(ct);
                    List<object?[]> batch;
                    try
                    {
                        batch = await ChunkedDiffEngine.ReadPageAsync(
                            srcRead, source, srcRef, srcCols, srcMeta.PrimaryKeyColumns, after,
                            Math.Max(100, options.BatchSize), selection.Where, ct);
                    }
                    finally { if (snapshotGate is not null) snapshotGate.Release(); }
                    if (batch.Count == 0) break;
                    after = KeyCodec.Encode(srcMeta.PrimaryKeyColumns, batch[^1], srcCols);
                    await channel.Writer.WriteAsync(batch, ct);
                    // 无主键表不分页（整表一次读入），读毕即结束，避免重复读全表
                    if (srcMeta.PrimaryKeyColumns.Count == 0) break;
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        });

        try
        {
            if (writer is not null)
            {
                await foreach (var batch in channel.Reader.ReadAllAsync(ct))
                {
                    foreach (var row in batch)
                    {
                        MapRow(row, srcCols, insertCols);
                        ApplyMask(row, maskByIndex, rnd);
                        await writer.WriteAsync(row, ct);
                    }
                    inserted += batch.Count;
                    onRows?.Invoke(inserted);
                }
                await writer.CompleteAsync(ct);
            }
            else
            {
                await foreach (var batch in channel.Reader.ReadAllAsync(ct))
                {
                    foreach (var row in batch)
                    {
                        MapRow(row, srcCols, insertCols);
                        ApplyMask(row, maskByIndex, rnd);
                    }
                    inserted += await RowOps.InsertRowsAsync(
                        tgtConn, null, target, tgtRef, insertCols, tgtConverted.Columns, batch, conflict, ct);
                    onRows?.Invoke(inserted);
                }
            }
            await producer;
        }
        finally
        {
            await ApplyPragmasAsync(target, tgtConn, on: false);
            if (writer is not null) await writer.DisposeAsync();
        }

        if (options.AlignAutoIncrement && tgtConverted.PrimaryKeyColumns.Count > 0)
        {
            var pk = tgtConverted.PrimaryKeyColumns[0];
            var maxVal = await ScalarMax(tgtConn, target, tgtRef, pk, ct);
            if (maxVal is > 0)
            {
                var stmts = target.Ddl.ResetAutoIncrement(tgtRef, pk, maxVal.Value);
                if (stmts.Count > 0)
                    await target.ExecuteDdlAsync(tgtRef.Database, stmts, ct);
            }
        }

        return new TableStats(inserted, 0, 0, warnings, channelName);
    }

    /// <summary>源列序 → 目标列序重排（源读列顺序与插入列顺序可能不同）。</summary>
    private static void MapRow(object?[] row, IReadOnlyList<string> srcCols, IReadOnlyList<string> insertCols)
    {
        var sameOrder = srcCols.Count == insertCols.Count;
        if (sameOrder)
        {
            for (var i = 0; i < srcCols.Count; i++)
            {
                if (!string.Equals(srcCols[i], insertCols[i], StringComparison.OrdinalIgnoreCase)) { sameOrder = false; break; }
            }
        }
        if (sameOrder) return;
        // 按插入列顺序从源行取值（同名列），缺列补 null
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < srcCols.Count; i++) index[srcCols[i]] = i;
        var mapped = new object?[insertCols.Count];
        for (var i = 0; i < insertCols.Count; i++)
            mapped[i] = index.TryGetValue(insertCols[i], out var j) ? row[j] : null;
        Array.Copy(mapped, row, Math.Min(mapped.Length, row.Length));
    }

    private static void ApplyMask(
        object?[] row, Dictionary<int, Func<Random, object?, object?>> mask, Random rnd)
    {
        if (mask.Count == 0) return;
        foreach (var (idx, fn) in mask)
        {
            if (idx < row.Length) row[idx] = fn(rnd, row[idx]);
        }
    }

    private static async Task ApplyPragmasAsync(IDbProvider target, DbConnection conn, bool on)
    {
        var stmts = on ? target.BulkLoadPragmas : ResetPragmas(target.Dialect);
        foreach (var s in stmts)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = s;
            try { await cmd.ExecuteNonQueryAsync(); }
            catch { /* 调优语句失败不阻塞同步 */ }
        }
    }

    private static IReadOnlyList<string> ResetPragmas(ArkDialect dialect) => dialect switch
    {
        // autocommit=1 提交遗留事务并释放 MDL：否则 AlignAutoIncrement 的 SELECT MAX 事务
        // 会与缓存连接上的 ALTER TABLE AUTO_INCREMENT 互相等 MDL，直到 60s 命令超时
        ArkDialect.MySQL => ["SET autocommit=1", "SET FOREIGN_KEY_CHECKS=1", "SET unique_checks=1"],
        ArkDialect.PostgreSQL => ["SET synchronous_commit=on"],
        ArkDialect.SQLite => ["PRAGMA foreign_keys=ON"],
        _ => [],
    };

    private static async Task<long?> ScalarMax(DbConnection conn, IDbProvider target, TableRef t, string pkCol, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MAX({target.Ddl.Quote(pkCol)}) FROM {target.Ddl.TableName(t)}";
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    // ------------------------------------------------------------------
    // 行级 Diff（委托分块引擎）
    // ------------------------------------------------------------------

    public Task<ChunkedDiffStats> DiffTableAsync(
        IDbProvider source, IDbProvider target,
        TableRef srcRef, CanonicalTable srcMeta,
        TableRef tgtRef, CanonicalTable tgtConverted,
        TableSelection selection, SyncPlanRequest options,
        ISnapshotSession? snapshot = null,
        Action<long, long, long, string?>? onWindow = null,
        string? resumeKey = null,
        bool apply = true,
        int maxDiffKeys = 0,
        int maxSamples = 0,
        CancellationToken ct = default,
        SemaphoreSlim? snapshotGate = null)
    {
        var warnings = new List<string>();
        var colPairs = BuildColumnPairs(srcMeta, tgtConverted, selection, warnings);
        var tgtColNames = colPairs.Select(p => p.Tgt).ToList();
        // 报告/校验模式同样需要掩码：目标端存的是脱敏值，哈希对比须用脱敏后的源值
        var mask = BuildMask(selection, tgtColNames, tgtConverted);
        var engine = new ChunkedDiffEngine();
        return engine.DiffAsync(
            source, target, srcRef, srcMeta, tgtRef, tgtConverted, colPairs,
            new ChunkedDiffOptions
            {
                WindowRows = options.ChunkRows,
                DeleteExtra = options.DeleteExtraRows,
                Apply = apply,
                Where = selection.Where,
                MaxDiffKeys = maxDiffKeys,
                MaxSamples = maxSamples,
                MaskByIndex = mask is { Count: > 0 } ? mask : null,
                MaskRandom = new Random(unchecked((int)DateTimeOffset.UtcNow.Ticks)),
            },
            snapshot,
            onWindow,
            resumeKey,
            ct,
            snapshotGate);
    }
}
