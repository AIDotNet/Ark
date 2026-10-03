using Ark.Api.Infrastructure;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;
using Ark.Sync;

namespace Ark.Api.Endpoints;

/// <summary>
/// 对比（先比后改）：POST /compare/runs 提交异步对比任务；
/// 差异键按需取行值；to-plan 由对比结果生成执行计划快照。
/// </summary>
public static class CompareEndpoints
{
    public static IEndpointRouteBuilder MapCompareEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/compare").WithTags("Compare");

        g.MapPost("/runs", (CompareRequest req, ArkRepository repo, SyncTaskManager mgr) =>
        {
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(req, repo);
            return mgr.SubmitCompare(req, srcSpec, tgtSpec, ProviderRegistry.Create).ToDto();
        });

        g.MapGet("/runs/{taskId:guid}", async (Guid taskId, SyncTaskManager mgr, CancellationToken ct) =>
        {
            var stored = await mgr.GetStoredAsync(taskId, ct)
                         ?? throw ArkException.NotFound($"对比任务 {taskId} 不存在");
            if (stored.State.Kind != TaskKind.Compare)
                throw ArkException.Validation("任务不是对比任务");
            return stored.State.ToDto();
        });

        // 差异行分页：按类型切片差异键，逐键从源/目标取行值
        g.MapGet("/runs/{taskId:guid}/tables/{table}/diffs", async (
            Guid taskId, string table, string? changeType, ArkRepository repo, SyncTaskManager mgr,
            int cursor = 0, int limit = 50, CancellationToken ct = default) =>
        {
            limit = limit is <= 0 or > 200 ? 50 : limit;
            changeType ??= "inserted";
            var stored = await mgr.GetStoredAsync(taskId, ct)
                         ?? throw ArkException.NotFound($"对比任务 {taskId} 不存在");
            var compare = stored.State.Compare
                          ?? throw ArkException.Validation("对比尚未完成");
            if (compare.DiffKeysByTable is null || !compare.DiffKeysByTable.TryGetValue(table, out var keys))
                throw ArkException.NotFound($"表 {table} 无差异键记录");

            var srcReq = stored.CompareReq ?? throw ArkException.Validation("缺少对比请求");
            var type = changeType.ToLowerInvariant() switch
            {
                "insert" or "inserted" => "inserted",
                "update" or "updated" => "updated",
                "delete" or "deleted" => "deleted",
                _ => throw ArkException.Validation($"不支持的差异类型: {changeType}"),
            };
            var allKeys = type switch
            {
                "inserted" => keys.Inserted,
                "updated" => keys.Updated,
                _ => keys.Deleted,
            };
            var page = allKeys.Skip(cursor).Take(limit).ToList();

            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(srcReq, repo);
            await using var source = ProviderRegistry.Create(srcSpec);
            await using var target = ProviderRegistry.Create(tgtSpec);

            var sel = srcReq.Tables.First(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));
            var tgtSchema = !string.IsNullOrWhiteSpace(srcReq.TargetSchema)
                ? srcReq.TargetSchema
                : target.Dialect == ArkDialect.PostgreSQL
                    ? (string.IsNullOrEmpty(sel.Schema) ? "public" : sel.Schema)
                    : null;
            var srcRef = new TableRef(srcReq.SourceDatabase, sel.Schema, sel.Table);
            var tgtRef = new TableRef(srcReq.TargetDatabase, tgtSchema, sel.Table);
            var srcMeta = await source.GetTableAsync(srcReq.SourceDatabase, sel.Schema, sel.Table, ct);
            var (converted, _) = TableConverter.Convert(srcMeta, source.Dialect, target.Dialect);
            var warnings = new List<string>();
            var colPairs = DataTransferEngine.BuildColumnPairs(srcMeta, converted, sel, warnings);
            var srcCols = colPairs.Select(p => p.Src).ToList();
            var tgtCols = colPairs.Select(p => p.Tgt).ToList();

            var rows = new List<object?[]>();
            foreach (var key in page)
            {
                var pkValues = KeyCodec.Decode(key);
                object?[]? srcRow = null, tgtRow = null;
                if (type != "deleted")
                    srcRow = await FetchRowAsync(source, srcRef, srcCols, srcMeta.PrimaryKeyColumns, pkValues, ct);
                if (type != "inserted")
                    tgtRow = await FetchRowAsync(target, tgtRef, tgtCols, srcMeta.PrimaryKeyColumns, pkValues, ct);
                rows.Add([key, srcRow, tgtRow]);
            }

            return new
            {
                table,
                changeType = type,
                total = allKeys.Count,
                truncated = type switch
                {
                    "inserted" => keys.InsertedTruncated,
                    "updated" => keys.UpdatedTruncated,
                    _ => keys.DeletedTruncated,
                },
                columns = srcCols,
                pkColumns = srcMeta.PrimaryKeyColumns,
                cursor = cursor + page.Count,
                rows = rows.Select(r => new
                {
                    key = r[0],
                    source = r[1] is object?[] s ? s.Select(CellValue.CanonicalString).ToArray() : null,
                    target = r[2] is object?[] t2 ? t2.Select(CellValue.CanonicalString).ToArray() : null,
                }).ToList(),
            };
        });

        // 由对比结果生成执行计划（默认同步全部差异 + 结构变更）
        g.MapPost("/runs/{taskId:guid}/to-plan", async (
            Guid taskId, ArkRepository repo, SyncTaskManager mgr, CancellationToken ct) =>
        {
            var stored = await mgr.GetStoredAsync(taskId, ct)
                         ?? throw ArkException.NotFound($"对比任务 {taskId} 不存在");
            var req = stored.CompareReq ?? throw ArkException.Validation("缺少对比请求");
            var planReq = new SyncPlanRequest
            {
                SourceConnectionId = req.SourceConnectionId,
                SourceDatabase = req.SourceDatabase,
                TargetConnectionId = req.TargetConnectionId,
                TargetDatabase = req.TargetDatabase,
                Mode = SyncMode.StructureAndData,
                DataMethod = DataSyncMethod.RowDiff,
                DeleteExtraRows = false,
                ChunkRows = req.ChunkRows,
                TargetSchema = req.TargetSchema,
                Tables = req.Tables,
            };
            var (srcSpec, tgtSpec) = SyncApi.ResolveSpecs(planReq, repo);
            await using var source = ProviderRegistry.Create(srcSpec);
            await using var target = ProviderRegistry.Create(tgtSpec);
            return await new SyncPlanner().BuildPlanAsync(source, target, planReq, ct);
        });

        return app;
    }

    private static async Task<object?[]?> FetchRowAsync(
        IDbProvider p, TableRef t, IReadOnlyList<string> cols,
        IReadOnlyList<string> pkCols, object?[] pkValues, CancellationToken ct)
    {
        await using var conn = await p.OpenConnectionAsync(t.Database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 30;
        var where = RowOps.BuildKeyInPredicate(p, pkCols, [pkValues], cmd);
        cmd.CommandText =
            "SELECT " + string.Join(", ", cols.Select(p.Ddl.Quote)) +
            " FROM " + p.Ddl.TableName(t) + " WHERE " + where + " LIMIT 2";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var row = new object?[r.FieldCount];
        r.GetValues(row);
        CellValue.NormalizeRow(row);
        return row;
    }
}
