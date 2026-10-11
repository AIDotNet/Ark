using System.Text;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Providers.DM8;

/// <summary>
/// 达梦 DM8 DDL 生成器（Oracle 风格语法）：标识符一律加双引号以保持大小写，
/// 自增用 IDENTITY(1,1)，改列用 MODIFY，外键单独 ALTER TABLE ADD CONSTRAINT。
/// </summary>
public sealed class Dm8DdlGenerator : IDdlGenerator
{
    public ArkDialect Dialect => ArkDialect.DM8;

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string TableName(TableRef t) =>
        string.IsNullOrEmpty(t.Schema) ? Quote(t.Table) : Quote(t.Schema) + "." + Quote(t.Table);

    public IReadOnlyList<string> CreateTable(TableRef target, CanonicalTable t, List<string> warnings)
    {
        // 把无条件定的表名替换为 schema 限定名（不破坏 Quote 的引号转义）
        var unqualified = Quote(t.Name);
        return CreateTable(t, warnings)
            .Select(s => s.Replace(unqualified, TableName(target), StringComparison.Ordinal))
            .ToList();
    }

    public IReadOnlyList<string> CreateTable(CanonicalTable t, List<string> warnings)
    {
        var statements = new List<string>();
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(Quote(t.Name)).AppendLine(" (");
        var defs = new List<string>();
        foreach (var col in t.Columns)
        {
            var def = ColumnDef(col, t, warnings);
            if (def is { } d) defs.Add("    " + d);
        }
        if (t.PrimaryKeyColumns.Count > 0)
            defs.Add("    PRIMARY KEY (" + string.Join(", ", t.PrimaryKeyColumns.Select(Quote)) + ")");
        sb.AppendLine(string.Join(",\n", defs));
        sb.Append(");");
        statements.Add(sb.ToString());

        foreach (var col in t.Columns.Where(c => c.Comment is { }))
            statements.Add($"COMMENT ON COLUMN {Quote(t.Name)}.{Quote(col.Name)} IS '{col.Comment!.Replace("'", "''")}';");

        return statements;
    }

    /// <summary>列定义；自增 → IDENTITY(1,1)；生成列 DM8 暂不支持，降级为普通列并警告。</summary>
    private string? ColumnDef(CanonicalColumn col, CanonicalTable t, List<string> warnings)
    {
        var sb = new StringBuilder(Quote(col.Name)).Append(' ');
        if (col.IsAutoIncrement)
        {
            if (col.Type.Id is CanonicalTypeId.Int16 or CanonicalTypeId.Int32 or CanonicalTypeId.Int64)
                sb.Append(TypeMapper.FromCanonical(col.Type, ArkDialect.DM8).Sql);
            else
            {
                warnings.Add($"列 {col.Name}: 自增列类型 {col.Type} 非整数，已改用 BIGINT identity");
                sb.Append("BIGINT");
            }
            sb.Append(" IDENTITY(1,1)");
            return sb.ToString();
        }

        var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.DM8);
        warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
        sb.Append(mapping.Sql);

        if (col.IsGenerated)
        {
            warnings.Add($"列 {col.Name}: DM8 生成列暂不支持自动迁移，已按普通列创建");
        }

        if (!col.Nullable) sb.Append(" NOT NULL");
        if (col.DefaultKind != ValueExprKind.None && col.DefaultValueSql is { } d)
        {
            var emitted = FunctionTranslator.Emit(ArkDialect.DM8, col.DefaultKind, d);
            if (emitted is null)
                warnings.Add($"列 {col.Name}: 默认值 {d} 无法在 DM8 表达，已忽略");
            else
                sb.Append(" DEFAULT ").Append(emitted);
        }
        return sb.ToString();
    }

    public IReadOnlyList<string> AlterTable(TableDiff diff, List<string> warnings)
    {
        var statements = new List<string>();
        var table = TableName(diff.Target);

        foreach (var col in diff.AddedColumns)
        {
            var def = ColumnDef(col, diff.After, warnings);
            if (def is not null) statements.Add($"ALTER TABLE {table} ADD {def};");
        }

        foreach (var col in diff.DroppedColumns)
            statements.Add($"ALTER TABLE {table} DROP COLUMN {Quote(col.Name)};");

        foreach (var change in diff.AlteredColumns)
        {
            var before = change.Before;
            var after = change.After;
            statements.AddRange(BuildAlterColumn(table, before, after, warnings));
        }

        if (diff.PrimaryKeyChanged)
            warnings.Add("DM8 主键变更涉及删除/重建约束与索引，v1 请人工处理");

        return statements;
    }

    /// <summary>DM 改列语法：ALTER TABLE t MODIFY c datatype [NOT NULL] [DEFAULT expr]。</summary>
    private List<string> BuildAlterColumn(string table, CanonicalColumn before, CanonicalColumn after, List<string> warnings)
    {
        var statements = new List<string>();
        var col = Quote(after.Name);

        if (!Equals(before.Type, after.Type))
        {
            var mapping = TypeMapper.FromCanonical(after.Type, ArkDialect.DM8);
            warnings.AddRange(mapping.Warnings.Select(w => $"列 {after.Name}: {w}"));
            statements.Add($"ALTER TABLE {table} MODIFY {col} {mapping.Sql};");
            warnings.Add($"列 {after.Name}: 类型变更 {before.Type} → {after.Type} 可能截断/转换失败现有数据");
        }

        if (before.Nullable != after.Nullable)
            statements.Add(after.Nullable
                ? $"ALTER TABLE {table} MODIFY {col} NULL;"
                : $"ALTER TABLE {table} MODIFY {col} NOT NULL;");

        if (!string.Equals(before.DefaultValueSql, after.DefaultValueSql, StringComparison.OrdinalIgnoreCase))
        {
            if (after.DefaultKind != ValueExprKind.None && after.DefaultValueSql is { } d)
            {
                var emitted = FunctionTranslator.Emit(ArkDialect.DM8, after.DefaultKind, d);
                if (emitted is null)
                    warnings.Add($"列 {after.Name}: 默认值 {d} 无法在 DM8 表达，已忽略");
                else
                    statements.Add($"ALTER TABLE {table} MODIFY {col} DEFAULT {emitted};");
            }
            else if (after.DefaultValueSql is null)
            {
                statements.Add($"ALTER TABLE {table} MODIFY {col} DEFAULT NULL;");
            }
        }

        return statements;
    }

    public IReadOnlyList<string> CreateIndex(CanonicalIndex idx, TableRef t, CanonicalTable? table = null)
    {
        var sql = $"CREATE {(idx.IsUnique ? "UNIQUE " : "")}INDEX {Quote(idx.Name)} ON {TableName(t)} " +
                  $"({string.Join(", ", idx.Columns.Select(Quote))})";
        return [sql + ";"];
    }

    public IReadOnlyList<string> DropIndex(string indexName, TableRef t)
    {
        // 索引与其表同 schema；带 schema 时限定以免落到别的模式
        var target = string.IsNullOrEmpty(t.Schema) ? Quote(indexName) : $"{Quote(t.Schema)}.{Quote(indexName)}";
        return [$"DROP INDEX {target};"];
    }

    public IReadOnlyList<string> DropTable(TableRef t) =>
        [$"DROP TABLE IF EXISTS {TableName(t)};"];

    public IReadOnlyList<string> CreateForeignKey(CanonicalForeignKey fk, TableRef t)
    {
        var sql = $"ALTER TABLE {TableName(t)} ADD CONSTRAINT {Quote(fk.Name)} FOREIGN KEY " +
                  $"({string.Join(", ", fk.Columns.Select(Quote))}) REFERENCES {QualifyRef(fk.ReferencedTable, t.Schema)} " +
                  $"({string.Join(", ", fk.ReferencedColumns.Select(Quote))})" +
                  (fk.OnDelete is { } od && od != "NO ACTION" ? $" ON DELETE {od}" : "");
        return [sql + ";"];
    }

    public IReadOnlyList<string> DropForeignKey(string fkName, TableRef t) =>
        [$"ALTER TABLE {TableName(t)} DROP CONSTRAINT {Quote(fkName)};"];

    public IReadOnlyList<string> TruncateTable(TableRef t) => [$"TRUNCATE TABLE {TableName(t)};"];

    /// <summary>DM8 的 IDENTITY 无 setval 等价语句：同步后不做自增对齐（返回空）。</summary>
    public IReadOnlyList<string> ResetAutoIncrement(TableRef t, string pkColumn, long value) => [];

    /// <summary>引用表可能带 schema 前缀（如 ark_sync.users）：分段引号；裸表名默认与目标同 schema。</summary>
    private string QualifyRef(string referenced, string? targetSchema)
    {
        var parts = referenced.Split('.');
        if (parts.Length > 1)
            return string.Join(".", parts.Select(Quote));
        return string.IsNullOrEmpty(targetSchema)
            ? Quote(referenced)
            : $"{Quote(targetSchema)}.{Quote(referenced)}";
    }
}
