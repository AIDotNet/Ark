using System.Text;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Providers.ClickHouse;

/// <summary>ClickHouse DDL 生成器：列类型经 TypeMapper（可空列包 Nullable），主键落在 MergeTree ORDER BY。</summary>
public sealed class ClickHouseDdlGenerator : IDdlGenerator
{
    public ArkDialect Dialect => ArkDialect.ClickHouse;

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>ClickHouse 库即命名空间：database 非空时输出 "db"."t"。</summary>
    public string TableName(TableRef t) =>
        (string.IsNullOrEmpty(t.Database) ? "" : Quote(t.Database) + ".") + Quote(t.Table);

    public IReadOnlyList<string> CreateTable(TableRef target, CanonicalTable t, List<string> warnings)
    {
        // 把无条件定的表名替换为库限定名（不破坏 Quote 的引号转义）
        var unqualified = Quote(t.Name);
        return CreateTable(t, warnings)
            .Select(s => s.Replace(unqualified, TableName(target), StringComparison.Ordinal))
            .ToList();
    }

    public IReadOnlyList<string> CreateTable(CanonicalTable t, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE IF NOT EXISTS ").Append(Quote(t.Name)).AppendLine(" (");
        var defs = new List<string>();
        foreach (var col in t.Columns)
            defs.Add("    " + ColumnDef(col, warnings));
        sb.AppendLine(string.Join(",\n", defs));
        sb.Append(") ENGINE = MergeTree ");
        if (t.PrimaryKeyColumns.Count > 0)
            sb.Append("ORDER BY (").Append(string.Join(", ", t.PrimaryKeyColumns.Select(Quote))).Append(')');
        else
        {
            sb.Append("ORDER BY tuple()");
            warnings.Add("ClickHouse 建议显式 ORDER BY：无主键表已按 ORDER BY tuple() 创建");
        }
        return [sb.ToString()];
    }

    /// <summary>完整列定义（建表/加列/改列共用）：可空列包 Nullable(...)，主键不是列级约束（由 ORDER BY 表达）。</summary>
    private string ColumnDef(CanonicalColumn col, List<string> warnings)
    {
        var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.ClickHouse);
        warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
        var sb = new StringBuilder(Quote(col.Name)).Append(' ');
        // ClickHouse 无 NOT NULL 语法：不可空即不包 Nullable；Nullable(UUID) 不合法需跳过
        if (col.Nullable && col.Type.Id != CanonicalTypeId.Guid)
            sb.Append("Nullable(").Append(mapping.Sql).Append(')');
        else
            sb.Append(mapping.Sql);

        if (col.IsGenerated)
        {
            warnings.Add($"列 {col.Name}: ClickHouse 生成列（MATERIALIZED/ALIAS）暂不支持同步，已按普通列创建");
        }
        else if (col.DefaultKind != ValueExprKind.None && col.DefaultValueSql is { } d)
        {
            var emitted = FunctionTranslator.Emit(ArkDialect.ClickHouse, col.DefaultKind, d);
            if (emitted is null)
                warnings.Add($"列 {col.Name}: 默认值 {d} 无法在 ClickHouse 表达，已忽略");
            else
                sb.Append(" DEFAULT ").Append(emitted);
        }
        if (col.IsAutoIncrement)
            warnings.Add($"列 {col.Name}: ClickHouse 无自增列，已忽略自增属性");
        return sb.ToString();
    }

    public IReadOnlyList<string> AlterTable(TableDiff diff, List<string> warnings)
    {
        var statements = new List<string>();
        var table = TableName(diff.Target);

        foreach (var col in diff.AddedColumns)
            statements.Add($"ALTER TABLE {table} ADD COLUMN {ColumnDef(col, warnings)}");

        foreach (var col in diff.DroppedColumns)
            statements.Add($"ALTER TABLE {table} DROP COLUMN {Quote(col.Name)}");

        foreach (var change in diff.AlteredColumns)
        {
            var before = change.Before;
            var after = change.After;
            if (before.Type != after.Type)
            {
                if (diff.After.PrimaryKeyColumns.Contains(after.Name, StringComparer.OrdinalIgnoreCase))
                    warnings.Add($"列 {after.Name}: 属于主键（ORDER BY），修改类型将触发数据重排序，请人工确认");
                statements.Add($"ALTER TABLE {table} MODIFY COLUMN {ColumnDef(after, warnings)}");
            }
            else if (before.Nullable != after.Nullable)
            {
                statements.Add($"ALTER TABLE {table} MODIFY COLUMN {ColumnDef(after, warnings)}");
            }
            else if (before.DefaultValueSql != after.DefaultValueSql && after.DefaultValueSql is not null)
            {
                statements.Add($"ALTER TABLE {table} MODIFY COLUMN {ColumnDef(after, warnings)}");
            }
        }

        if (diff.PrimaryKeyChanged)
            warnings.Add("ClickHouse 主键由 ORDER BY 决定，不支持直接修改（如需变更请重建表）");

        return statements;
    }

    public IReadOnlyList<string> CreateIndex(CanonicalIndex idx, TableRef t, CanonicalTable? table = null)
    {
        // ClickHouse 无二级索引（SKIP INDEX 暂不支持）：返回空，索引被跳过
        return [];
    }

    public IReadOnlyList<string> DropIndex(string indexName, TableRef t) => [];

    public IReadOnlyList<string> DropTable(TableRef t) =>
        [$"DROP TABLE IF EXISTS {TableName(t)};"];

    public IReadOnlyList<string> CreateForeignKey(CanonicalForeignKey fk, TableRef t)
    {
        // ClickHouse 无外键约束
        return [];
    }

    public IReadOnlyList<string> DropForeignKey(string fkName, TableRef t) => [];

    public IReadOnlyList<string> TruncateTable(TableRef t) => [$"TRUNCATE TABLE {TableName(t)}"];

    public IReadOnlyList<string> ResetAutoIncrement(TableRef t, string pkColumn, long value) => [];
}
