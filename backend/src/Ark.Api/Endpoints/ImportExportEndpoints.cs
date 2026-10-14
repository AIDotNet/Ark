using Ark.Api.Filters;
using Ark.Api.Infrastructure;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Sync;

namespace Ark.Api.Endpoints;

public static class ImportExportEndpoints
{
    public sealed record ExportRequest(string Format, long? Limit);

    public static IEndpointRouteBuilder MapImportExportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/connections/{id:guid}/databases/{db}/schemas/{schema}/tables/{table}")
            .WithTags("ImportExport");

        // 库/Schema 级批量导出：schema 缺省 = 库内全部非系统 schema（SupportsSchemas 方言）或默认 schema。
        // SQL 为单文件 dump（建表+数据，外键依赖排序）；CSV/JSON 为 ZIP（每表一个文件）。只导普通表。
        app.MapPost("/connections/{id:guid}/databases/{db}/export", [SkipEnvelope] async (
            HttpContext http, Guid id, string db, string? schema,
            ExportRequest req, ArkRepository repo, CancellationToken ct) =>
        {
            if (req.Limit is < 0) throw ArkException.Validation($"limit 不能为负数: {req.Limit}");
            var format = req.Format.ToLowerInvariant();
            if (format is not ("csv" or "json" or "sql"))
                throw ArkException.Validation($"不支持的导出格式: {req.Format}");
            await ProviderRequest.UseAsync<object?>(id, repo, repo.Pw(), async p =>
            {
                var schemaNames = new List<string>();
                if (!string.IsNullOrWhiteSpace(schema))
                {
                    schemaNames.Add(schema);
                }
                else if (p.Capabilities.SupportsSchemas)
                {
                    schemaNames.AddRange((await p.ListSchemasAsync(db, ct)).Select(s => s.Name));
                }
                else
                {
                    schemaNames.Add(Dialects.Get(p.Dialect).DefaultTargetSchema ?? db);
                }

                var tables = new List<(TableRef, CanonicalTable)>();
                foreach (var s in schemaNames)
                {
                    foreach (var t in await p.ListTablesAsync(db, s, ct))
                    {
                        if (t.Kind != "table") continue;
                        tables.Add((new TableRef(db, s, t.Name), await p.GetTableAsync(db, s, t.Name, ct)));
                    }
                }
                if (tables.Count == 0) throw ArkException.Validation("该范围内没有可导出的表");

                var scopeName = string.IsNullOrWhiteSpace(schema) ? db : $"{db}.{schema}";
                var fileName = $"{scopeName}.{(format == "sql" ? "sql" : "zip")}";
                http.Response.ContentType = format == "sql"
                    ? "application/sql; charset=utf-8"
                    : "application/zip";
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                await Exporter.ExportManyAsync(http.Response.Body, format, p, tables, req.Limit, ct);
                return null;
            }, ct);
        }).WithTags("ImportExport");

        // 导出：文件流下载（跳过包络包装）
        g.MapPost("/export", [SkipEnvelope] async (
            HttpContext http, Guid id, string db, string schema, string table,
            ExportRequest req, ArkRepository repo, CancellationToken ct) =>
        {
            Identifier.EnsureValid(table, "表名");
            if (req.Limit is < 0) throw ArkException.Validation($"limit 不能为负数: {req.Limit}");
            var format = req.Format.ToLowerInvariant();
            await ProviderRequest.UseAsync<object?>(id, repo, repo.Pw(), async p =>
            {
                var meta = await p.GetTableAsync(db, schema, table, ct);
                var tref = new TableRef(db, schema, table);
                var fileName = $"{table}.{(format == "sql" ? "sql" : format)}";
                var contentType = format switch
                {
                    "csv" => "text/csv; charset=utf-8",
                    "json" => "application/json; charset=utf-8",
                    "sql" => "application/sql; charset=utf-8",
                    _ => throw ArkException.Validation($"不支持的导出格式: {req.Format}"),
                };
                http.Response.ContentType = contentType;
                http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
                await Exporter.ExportAsync(http.Response.Body, format, p, tref, meta, req.Limit, ct);
                return null;
            }, ct);
        });

        // 导入：multipart 上传 CSV/JSON（IFormFile 会自动注入 antiforgery 元数据，
        // 而本应用未启用 antiforgery 中间件 → 必须 DisableAntiforgery，否则一律 500。
        // 单机单用户工具，无 CSRF 暴露面，关闭是正确选择）
        g.MapPost("/import", async (
            Guid id, string db, string schema, string table,
            IFormFile file, string? format, int? batchSize,
            ArkRepository repo, CancellationToken ct) =>
        {
            Identifier.EnsureValid(table, "表名");
            if (file is null || file.Length == 0) throw ArkException.Validation("请上传文件");
            var fmt = (format ?? Path.GetExtension(file.FileName).TrimStart('.')).ToLowerInvariant();
            await using var stream = file.OpenReadStream();
            return await ProviderRequest.UseAsync(id, repo, repo.Pw(), p =>
                Importer.ImportAsync(stream, fmt, table, p, new TableRef(db, schema, table), batchSize ?? 500, ct), ct);
        }).DisableAntiforgery();

        return app;
    }
}
