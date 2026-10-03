using Ark.Api.Infrastructure;
using Ark.Core.Dtos;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;
using Ark.Sync;

namespace Ark.Api.Endpoints;

/// <summary>表结构设计器：建表 / 改表（Diff → DDL 预览 → 执行）。</summary>
public static class TableEndpoints
{
    public static IEndpointRouteBuilder MapTableEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/connections/{id:guid}/databases/{db}/schemas/{schema}/tables").WithTags("Tables");

        // 预览建表 DDL
        g.MapPost("/preview", (Guid id, string db, string schema, CreateTableRequest req, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                var warnings = new List<string>();
                var statements = BuildCreateStatements(p, db, schema, req.Table, warnings);
                return new DdlPreviewResponse(statements, warnings.Distinct().ToList());
            }, ct));

        // 建表（execute=true 执行）
        g.MapPost("/", async (Guid id, string db, string schema, CreateTableRequest req, ArkRepository repo, CancellationToken ct) =>
            await ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                var warnings = new List<string>();
                var statements = BuildCreateStatements(p, db, schema, req.Table, warnings).ToList();
                if (!req.Execute) return new DdlExecuteResponse(statements, 0, [], warnings.Distinct().ToList());
                var (executed, errors) = await p.ExecuteDdlAsync(db, statements, ct);
                return new DdlExecuteResponse(statements, executed, errors, warnings.Distinct().ToList());
            }, ct));

        // 预览改表 DDL（现有表 vs 提交模型）
        g.MapPut("/{table}/preview", (Guid id, string db, string schema, string table, AlterTableRequest req, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                var existing = await p.GetTableAsync(db, schema, table, ct);
                var warnings = new List<string>();
                var statements = BuildAlterStatements(p, existing, req.Table, new TableRef(db, schema, table), warnings);
                return new DdlPreviewResponse(statements, warnings.Distinct().ToList());
            }, ct));

        // 改表
        g.MapPut("/{table}", async (Guid id, string db, string schema, string table, AlterTableRequest req, ArkRepository repo, CancellationToken ct) =>
            await ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                var existing = await p.GetTableAsync(db, schema, table, ct);
                var warnings = new List<string>();
                var statements = BuildAlterStatements(p, existing, req.Table, new TableRef(db, schema, table), warnings).ToList();
                if (!req.Execute) return new DdlExecuteResponse(statements, 0, [], warnings.Distinct().ToList());
                var (executed, errors) = await p.ExecuteDdlAsync(db, statements, ct);
                return new DdlExecuteResponse(statements, executed, errors, warnings.Distinct().ToList());
            }, ct));

        // 删除表
        g.MapDelete("/{table}", async (Guid id, string db, string schema, string table, ArkRepository repo, CancellationToken ct) =>
            await ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                Identifier.EnsureValid(table, "表名");
                var statements = p.Ddl.DropTable(new TableRef(db, schema, table));
                var (executed, errors) = await p.ExecuteDdlAsync(db, statements, ct);
                return new DdlExecuteResponse(statements, executed, errors, []);
            }, ct));

        return app;
    }

    /// <summary>
    /// 建表完整语句：CREATE TABLE + 模型内声明的二级索引 + 外键。
    /// CREATE TABLE 只含主键约束：二级索引逐条 CreateIndex 追加（MySQL TEXT/JSON 列
    /// 由生成器按 CanonicalTable 自动加前缀长度）；外键由各生成器按方言表达 ——
    /// SQLite 内联在 CREATE TABLE 里（其 CreateForeignKey 返回空，此处跳过），Pg/MySQL 追加 ALTER 语句。
    /// </summary>
    private static IReadOnlyList<string> BuildCreateStatements(
        IDbProvider p, string db, string schema, CanonicalTable table, List<string> warnings)
    {
        Identifier.EnsureValid(table.Name, "表名");
        var statements = p.Ddl.CreateTable(table, warnings).ToList();
        var tref = new TableRef(db, schema, table.Name);
        foreach (var idx in table.Indexes.Where(i => !i.IsPrimaryKey))
            statements.AddRange(p.Ddl.CreateIndex(idx, tref, table));
        if (p.Dialect != ArkDialect.SQLite) // SQLite 的外键已内联在 CREATE TABLE 中
            foreach (var fk in table.ForeignKeys)
                statements.AddRange(p.Ddl.CreateForeignKey(fk, tref));
        return statements;
    }

    /// <summary>
    /// 改表完整语句：列级 Diff（AlterTable）+ Diff 中的索引/外键变更。
    /// AlterTable 只处理列级变更；索引走 CreateIndex、外键走 CreateForeignKey ——
    /// 生成器不支持时（SQLite 无法对既有表追加外键）返回空语句，必须显式警告而不是静默。
    /// </summary>
    private static IReadOnlyList<string> BuildAlterStatements(
        IDbProvider p, CanonicalTable existing, CanonicalTable after, TableRef target, List<string> warnings)
    {
        var diff = StructureDiffer.Diff(existing, after, target);
        var statements = p.Ddl.AlterTable(diff, warnings).ToList();
        foreach (var idx in diff.AddedIndexes.Where(i => !i.IsPrimaryKey))
            statements.AddRange(p.Ddl.CreateIndex(idx, diff.Target, diff.After));
        foreach (var fk in diff.AddedForeignKeys)
        {
            var fkStmts = p.Ddl.CreateForeignKey(fk, diff.Target);
            if (fkStmts.Count == 0)
                warnings.Add($"{p.Dialect} 不支持为既有表追加外键（{fk.Name} → {fk.ReferencedTable}），请重建表或人工处理");
            statements.AddRange(fkStmts);
        }
        return statements;
    }
}
