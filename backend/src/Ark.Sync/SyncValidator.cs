using System.Data.Common;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

/// <summary>同步后校验：行数对比（必做）+ 抽样窗口哈希复检。</summary>
public static class SyncValidator
{
    public static async Task<long> CountRowsAsync(
        IDbProvider p, DbConnection conn, TableRef t, string? where, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        var whereSql = string.IsNullOrWhiteSpace(where) ? "" : $" WHERE ({where})";
        cmd.CommandText = $"SELECT COUNT(*) FROM {p.Ddl.TableName(t)}{whereSql}";
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    /// <summary>校验一张表：行数对比；Sample 模式额外对首窗口做只读 Diff 复检。</summary>
    public static async Task<VerifyResult?> VerifyAsync(
        IDbProvider source, IDbProvider target,
        TableRef srcRef, CanonicalTable srcMeta,
        TableRef tgtRef, CanonicalTable tgtConverted,
        IReadOnlyList<(string Src, string Tgt)> colPairs,
        VerifyMode mode, string? where, int windowRows,
        ISnapshotSession? snapshot = null,
        Dictionary<int, Func<Random, object?, object?>>? mask = null,
        CancellationToken ct = default,
        SemaphoreSlim? snapshotGate = null)
    {
        if (mode == VerifyMode.Off) return null;
        await using var srcConn = snapshot is not null ? null : await source.OpenConnectionAsync(srcRef.Database, ct);
        await using var tgtConn = await target.OpenConnectionAsync(tgtRef.Database, ct);
        // 快照会话是单条物理连接：多表并行时源端读必须互斥
        if (snapshotGate is not null) await snapshotGate.WaitAsync(ct);
        long srcCount;
        try
        {
            srcCount = await CountRowsAsync(source, snapshot?.Connection ?? srcConn!, srcRef, where, ct);
        }
        finally { if (snapshotGate is not null) snapshotGate.Release(); }
        var tgtCount = await CountRowsAsync(target, tgtConn, tgtRef, null, ct);

        bool? sampleMatch = null;
        // 抽样复检走行级 Diff（依赖主键匹配行）；无主键表退化为仅行数对比
        if ((mode is VerifyMode.Sample or VerifyMode.Full) && srcMeta.PrimaryKeyColumns.Count > 0)
        {
            var engine = new ChunkedDiffEngine();
            var stats = await engine.DiffAsync(
                source, target, srcRef, srcMeta, tgtRef, tgtConverted, colPairs,
                new ChunkedDiffOptions
                {
                    WindowRows = windowRows,
                    DeleteExtra = false,
                    Apply = false,
                    MaxDiffKeys = 0,
                    MaxSamples = 0,
                    Where = where,
                    SingleWindow = mode == VerifyMode.Sample, // 全量校验交给对比任务，这里只抽样
                    MaskByIndex = mask,
                    MaskRandom = new Random(unchecked((int)DateTimeOffset.UtcNow.Ticks)),
                },
                snapshot,
                ct: ct,
                snapshotGate: snapshotGate);
            sampleMatch = stats.Inserted + stats.Updated + stats.Deleted == 0;
        }
        return new VerifyResult(srcCount, tgtCount, srcCount == tgtCount, sampleMatch);
    }
}
