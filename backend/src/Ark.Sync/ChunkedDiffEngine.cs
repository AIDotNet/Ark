using System.Data.Common;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

public sealed record ChunkedDiffOptions
{
    /// <summary>窗口行数（源端每页读取行数，目标侧窗口按源页上界对齐）。</summary>
    public int WindowRows { get; init; } = 2000;
    public bool DeleteExtra { get; init; }
    /// <summary>true=逐窗口应用变更；false=仅报告（对比模式）。</summary>
    public bool Apply { get; init; } = true;
    public string? Where { get; init; }
    public int MaxDiffKeys { get; init; } = 2000;
    public int MaxSamples { get; init; } = 20;
    /// <summary>只处理首个窗口后停止（校验抽样用）。</summary>
    public bool SingleWindow { get; init; }
    /// <summary>写目标前应用的脱敏变换（列序 → 变换）。Diff 哈希仍用源值。</summary>
    public Dictionary<int, Func<Random, object?, object?>>? MaskByIndex { get; init; }
    public Random? MaskRandom { get; init; }
}

public sealed record ChunkedDiffStats
{
    public long Inserted { get; init; }
    public long Updated { get; init; }
    public long Deleted { get; init; }
    public long Windows { get; init; }
    public long DiffWindows { get; init; }
    public string? LastKey { get; init; }
    public IReadOnlyList<DiffSampleRow> Samples { get; init; } = [];
    public DiffKeys Keys { get; init; } = DiffKeys.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 分块流式 Diff（keyset merge-join）：
/// 以源端页上界为窗口，两侧在窗口内做集合比对 —— 内存 O(窗口)，翻页 O(n)（无 OFFSET 扫描）。
/// 窗口对齐不依赖键编码排序：范围一律由数据库谓词裁剪，窗口内用字典匹配。
/// </summary>
public sealed class ChunkedDiffEngine
{
    public async Task<ChunkedDiffStats> DiffAsync(
        IDbProvider source, IDbProvider target,
        TableRef srcRef, CanonicalTable srcMeta,
        TableRef tgtRef, CanonicalTable tgtConverted,
        IReadOnlyList<(string Src, string Tgt)> colPairs,
        ChunkedDiffOptions opt,
        ISnapshotSession? snapshot = null,
        Action<long, long, long, string?>? onWindow = null,
        string? resumeAfterKey = null,
        CancellationToken ct = default,
        SemaphoreSlim? snapshotGate = null)
    {
        var warnings = new List<string>();
        var pkSrc = srcMeta.PrimaryKeyColumns;
        var map = colPairs.ToDictionary(p => p.Src, p => p.Tgt, StringComparer.OrdinalIgnoreCase);
        var pkTgt = pkSrc.Select(c => map.TryGetValue(c, out var t) ? t : c).ToList();
        var srcCols = colPairs.Select(p => p.Src).ToList();
        var tgtCols = colPairs.Select(p => p.Tgt).ToList();
        // 行哈希前按目标列的规范类型归一（跨方言时间戳/decimal/GUID 的 CLR 表示不同）
        var hashTypes = tgtCols
            .Select(c => tgtConverted.Columns.FirstOrDefault(x => string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase))?.Type.Id)
            .ToArray();
        var mask = opt.MaskByIndex;
        var rnd = opt.MaskRandom;

        await using var srcConn = snapshot is not null ? null : await source.OpenConnectionAsync(srcRef.Database, ct);
        var srcRead = snapshot?.Connection ?? srcConn!;
        await using var tgtConn = await target.OpenConnectionAsync(tgtRef.Database, ct);

        var srcAfter = resumeAfterKey;
        var tgtAfter = resumeAfterKey;

        long ins = 0, upd = 0, del = 0, windows = 0, diffWindows = 0;
        var samples = new List<DiffSampleRow>();
        var keysIns = new List<string>();
        var keysUpd = new List<string>();
        var keysDel = new List<string>();
        var truncIns = false;
        var truncUpd = false;
        var truncDel = false;
        string? lastKey = resumeAfterKey;
        // 无主键表：行哈希比对与行顺序无关，不做 keyset 分页（整表一次读入，单窗口）
        var hasPk = pkSrc.Count > 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<object?[]> srcPage;
            // 快照会话是单条物理连接：多表并行时源端读必须互斥，避免连接被并发占用
            if (snapshotGate is not null) await snapshotGate.WaitAsync(ct);
            try
            {
                srcPage = await ReadPageAsync(srcRead, source, srcRef, srcCols, pkSrc, srcAfter, opt.WindowRows, opt.Where, ct);
            }
            finally { if (snapshotGate is not null) snapshotGate.Release(); }

            // 源耗尽 → 目标剩余行全部为删除。检测必须无条件走完（compare 是「检测」，
            // 尾部 target 独有行也要计入 deleted 与 diffs 明细）；是否物理删除由
            // DeleteExtra（应用选项）在 ApplyWindowAsync 内决定，不在此处截断
            if (srcPage.Count == 0)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var rest = await ReadTargetRangeAsync(tgtConn, target, tgtRef, tgtCols, pkTgt, tgtAfter, null, opt.WindowRows, ct);
                    if (rest.Count == 0) break;
                    var restKeys = rest.Select(r => KeyCodec.Encode(pkTgt, r, tgtCols)).ToList();
                    await ApplyWindowAsync(tgtConn, target, tgtRef, pkTgt, tgtCols, tgtConverted,
                        inserts: [], updates: [], deletes: restKeys.Select(KeyCodec.Decode).ToList(), apply: opt.Apply, opt, ct);
                    del += restKeys.Count;
                    if (!opt.Apply) Track(keysDel, restKeys, opt.MaxDiffKeys, ref truncDel);
                    lastKey = restKeys[^1];
                    tgtAfter = lastKey;
                    windows++;
                    diffWindows++;
                    onWindow?.Invoke(ins, upd, del, lastKey);
                    if (rest.Count < opt.WindowRows) break;
                    if (pkTgt.Count == 0) break; // 无主键：目标整表已一次读完
                }
                break;
            }

            var upper = KeyCodec.Encode(pkSrc, srcPage[^1], srcCols);
            lastKey = upper;

            // 目标窗口：pk > tgtAfter AND pk <= upper（分页读取直至到达上界）
            var tgtRows = new List<object?[]>();
            while (true)
            {
                var page = await ReadTargetRangeAsync(tgtConn, target, tgtRef, tgtCols, pkTgt, tgtAfter, upper, opt.WindowRows, ct);
                if (page.Count == 0) break;
                tgtRows.AddRange(page);
                tgtAfter = KeyCodec.Encode(pkTgt, page[^1], tgtCols);
                if (page.Count < opt.WindowRows) break; // 未满页 = 已越过上界
                if (!hasPk) break; // 无主键：目标整表已一次读完
            }

            // 窗口内比对（字典匹配，不依赖顺序）
            var tgtIndex = new Dictionary<string, object?[]>(tgtRows.Count, StringComparer.Ordinal);
            foreach (var r in tgtRows)
                tgtIndex[KeyCodec.Encode(pkTgt, r, tgtCols)] = r;

            var toInsert = new List<object?[]>();
            var toUpdate = new List<object?[]>();
            var toDeleteRows = new List<object?[]>();
            var toDeleteKeyStrs = new List<string>();
            foreach (var row in srcPage)
            {
                var key = KeyCodec.Encode(pkSrc, row, srcCols);
                if (tgtIndex.Remove(key, out var tgtRow))
                {
                    // 脱敏场景：目标端存的就是脱敏值，哈希须用"脱敏后的源值"对比，否则每轮误更新
                    var srcHashRow = mask is { Count: > 0 } ? MaskClone(row, mask, rnd ?? Random.Shared) : row;
                    if (RowHash.ComputeRow(tgtCols, HashValues.NormalizeRow(srcHashRow, hashTypes), tgtCols) ==
                        RowHash.ComputeRow(tgtCols, HashValues.NormalizeRow(tgtRow, hashTypes), tgtCols))
                        continue;
                    toUpdate.Add(row);
                    if (opt.MaxDiffKeys > 0) Track(keysUpd, [key], opt.MaxDiffKeys, ref truncUpd);
                    AddSample(samples, "update", key, row, tgtRow, srcCols, tgtCols, opt.MaxSamples);
                }
                else
                {
                    toInsert.Add(row);
                    if (opt.MaxDiffKeys > 0) Track(keysIns, [key], opt.MaxDiffKeys, ref truncIns);
                    AddSample(samples, "insert", key, row, null, srcCols, tgtCols, opt.MaxSamples);
                }
            }
            // 目标窗口内剩余 = 源端没有的键 → 删除
            foreach (var kv in tgtIndex)
            {
                toDeleteRows.Add(kv.Value);
                toDeleteKeyStrs.Add(kv.Key);
                if (opt.MaxDiffKeys > 0) Track(keysDel, [kv.Key], opt.MaxDiffKeys, ref truncDel);
                AddSample(samples, "delete", kv.Key, null, kv.Value, srcCols, tgtCols, opt.MaxSamples);
            }

            var isDiff = toInsert.Count + toUpdate.Count + toDeleteRows.Count > 0;
            if (isDiff)
                await ApplyWindowAsync(tgtConn, target, tgtRef, pkTgt, tgtCols, tgtConverted,
                    toInsert, toUpdate,
                    toDeleteKeyStrs.Select(KeyCodec.Decode).ToList(), opt.Apply, opt, ct);

            ins += toInsert.Count;
            upd += toUpdate.Count;
            del += toDeleteRows.Count;
            windows++;
            if (isDiff) diffWindows++;
            onWindow?.Invoke(ins, upd, del, lastKey);

            srcAfter = upper;
            tgtAfter = upper;
            if (opt.SingleWindow) break;
            if (!hasPk) break; // 无主键：源整表已一次读完，单窗口即结束
            // 短页（含恰好耗尽）不在此 break：下一轮 srcPage 为空 → 进入 delete-drain 分支处理目标端多余行
        }

        return new ChunkedDiffStats
        {
            Inserted = ins,
            Updated = upd,
            Deleted = del,
            Windows = windows,
            DiffWindows = diffWindows,
            LastKey = lastKey,
            Samples = samples,
            Keys = new DiffKeys(keysIns, truncIns, keysUpd, truncUpd, keysDel, truncDel),
            Warnings = warnings,
        };
    }

    // ------------------------------------------------------------------

    private static void Track(List<string> into, List<string> add, int max, ref bool truncated)
    {
        foreach (var k in add)
        {
            if (into.Count >= max) { truncated = true; return; }
            into.Add(k);
        }
    }

    private static void AddSample(
        List<DiffSampleRow> samples, string type, string key,
        object?[]? srcRow, object?[]? tgtRow,
        IReadOnlyList<string> srcCols, IReadOnlyList<string> tgtCols, int max)
    {
        if (samples.Count >= max) return;
        samples.Add(new DiffSampleRow(
            key,
            type,
            srcRow?.Select(CellValue.CanonicalString).ToArray() ?? [],
            tgtRow?.Select(CellValue.CanonicalString).ToArray() ?? []));
    }

    /// <summary>
    /// 窗口内应用：同一事务里执行 删除→插入→更新（先删后插避免主键冲突）。
    /// DeleteExtra=false 为 upsert-only 合并语义：跳过物理删除（统计照常报告差异），
    /// 该开关只约束「应用」，不影响 Diff 检测。
    /// </summary>
    private static async Task<long> ApplyWindowAsync(
        DbConnection conn, IDbProvider target, TableRef tgtRef,
        IReadOnlyList<string> pkTgt, IReadOnlyList<string> tgtCols, CanonicalTable tgtConverted,
        List<object?[]> inserts, List<object?[]> updates, List<object?[]> deletes,
        bool apply, ChunkedDiffOptions opt, CancellationToken ct)
    {
        if (!apply) return 0;
        if (inserts.Count + updates.Count + deletes.Count == 0) return 0;
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var n = 0L;
            if (opt.DeleteExtra)
                n += await RowOps.DeleteRowsAsync(conn, tx, target, tgtRef, pkTgt, deletes, ct);
            var mask = opt?.MaskByIndex;
            var rnd = opt?.MaskRandom;
            if (mask is { Count: > 0 })
            {
                // 仅写路径脱敏：克隆行后变换，Diff 哈希仍用源值
                inserts = inserts.Select(r => MaskClone(r, mask, rnd)).ToList();
                updates = updates.Select(r => MaskClone(r, mask, rnd)).ToList();
            }
            n += await RowOps.InsertRowsAsync(conn, tx, target, tgtRef, tgtCols, tgtConverted.Columns, inserts, ConflictMode.Error, ct);
            foreach (var row in updates)
                n += await RowOps.UpdateRowAsync(conn, tx, target, tgtRef, pkTgt, tgtCols, tgtConverted.Columns, row, ct);
            await tx.CommitAsync(ct);
            return n;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static object?[] MaskClone(
        object?[] row, Dictionary<int, Func<Random, object?, object?>> mask, Random? rnd)
    {
        var clone = (object?[])row.Clone();
        foreach (var (idx, fn) in mask)
        {
            if (idx < clone.Length) clone[idx] = fn(rnd ?? Random.Shared, clone[idx]);
        }
        return clone;
    }

    /// <summary>
    /// 源端 keyset 分页（ORDER BY pk LIMIT n，可选 WHERE 过滤）。
    /// 无主键表退化为整表一次读入（无 ORDER BY / LIMIT，行序无意义，比对按行哈希）。
    /// </summary>
    public static async Task<List<object?[]>> ReadPageAsync(
        DbConnection conn, IDbProvider p, TableRef t,
        IReadOnlyList<string> colNames, IReadOnlyList<string> pkCols,
        string? afterKey, int limit, string? where, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        var whereSql = string.IsNullOrWhiteSpace(where) ? "" : $" WHERE ({where})";
        if (pkCols.Count == 0)
        {
            cmd.CommandText =
                "SELECT " + string.Join(", ", colNames.Select(p.Ddl.Quote)) +
                " FROM " + p.Ddl.TableName(t) + whereSql;
        }
        else
        {
            var after = KeyCodec.KeysetAfter(p, pkCols, afterKey, cmd);
            var afterAndWhere = string.IsNullOrWhiteSpace(where) ? "" : $" AND ({where})";
            cmd.CommandText =
                "SELECT " + string.Join(", ", colNames.Select(p.Ddl.Quote)) +
                " FROM " + p.Ddl.TableName(t) +
                (after.Length > 0 || afterAndWhere.Length > 0 ? " WHERE " + (after.Length > 0 ? after : "1=1") + afterAndWhere : "") +
                " ORDER BY " + string.Join(", ", pkCols.Select(p.Ddl.Quote)) +
                $" LIMIT {limit}";
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            CellValue.NormalizeRow(row);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// 目标端范围分页：pk &gt; after [AND pk &lt;= upper]。
    /// 无主键表退化为整表一次读入（无谓词 / ORDER BY / LIMIT）。
    /// </summary>
    private static async Task<List<object?[]>> ReadTargetRangeAsync(
        DbConnection conn, IDbProvider p, TableRef t,
        IReadOnlyList<string> colNames, IReadOnlyList<string> pkCols,
        string? afterKey, string? upperKey, int limit, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        if (pkCols.Count == 0)
        {
            cmd.CommandText =
                "SELECT " + string.Join(", ", colNames.Select(p.Ddl.Quote)) +
                " FROM " + p.Ddl.TableName(t);
        }
        else
        {
            var conds = new List<string>();
            var after = KeyCodec.KeysetAfter(p, pkCols, afterKey, cmd);
            if (after.Length > 0) conds.Add(after);
            if (upperKey is not null) conds.Add(KeyCodec.KeysetUpper(p, pkCols, upperKey, cmd));
            cmd.CommandText =
                "SELECT " + string.Join(", ", colNames.Select(p.Ddl.Quote)) +
                " FROM " + p.Ddl.TableName(t) +
                (conds.Count > 0 ? " WHERE " + string.Join(" AND ", conds) : "") +
                " ORDER BY " + string.Join(", ", pkCols.Select(p.Ddl.Quote)) +
                $" LIMIT {limit}";
        }
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            CellValue.NormalizeRow(row);
            rows.Add(row);
        }
        return rows;
    }
}
