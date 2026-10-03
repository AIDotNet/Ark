using System.Text;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Providers.MySQL;

public sealed class MySqlDdlGenerator : IDdlGenerator
{
    public ArkDialect Dialect => ArkDialect.MySQL;

    public string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    public string TableName(TableRef t) =>
        (string.IsNullOrEmpty(t.Database) ? "" : Quote(t.Database) + ".") + Quote(t.Table);

    public IReadOnlyList<string> CreateTable(TableRef target, CanonicalTable t, List<string> warnings)
    {
        // 把无条件定的表名替换为 schema/库限定名（不破坏 Quote 的引号转义）
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
            defs.Add("    " + ColumnDef(col, t, warnings));
        if (t.PrimaryKeyColumns.Count > 0)
            defs.Add("    PRIMARY KEY (" + string.Join(", ", t.PrimaryKeyColumns.Select(Quote)) + ")");
        sb.AppendLine(string.Join(",\n", defs));
        sb.Append(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci");
        if (t.Comment is { } c) sb.Append(" COMMENT='").Append(c.Replace("'", "''")).Append('\'');
        return [sb.ToString() + ";"];
    }

    /// <summary>完整列定义（建表/加列/改列共用）。</summary>
    private string ColumnDef(CanonicalColumn col, CanonicalTable t, List<string> warnings)
    {
        var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.MySQL);
        warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
        var sb = new StringBuilder(Quote(col.Name)).Append(' ').Append(mapping.Sql);
        if (col.IsGenerated)
        {
            if (col.GeneratedExpression is { } expr)
            {
                sb.Append(" GENERATED ALWAYS AS (").Append(expr).Append(") STORED");
                return sb.ToString();
            }
            warnings.Add($"列 {col.Name}: 生成列缺少表达式，已按普通列创建");
        }
        if (!col.Nullable) sb.Append(" NOT NULL");
        if (col.DefaultKind != ValueExprKind.None && col.DefaultValueSql is { } d)
        {
            var emitted = FunctionTranslator.Emit(ArkDialect.MySQL, col.DefaultKind, d);
            if (emitted is null)
            {
                warnings.Add($"列 {col.Name}: 默认值 {d} 无法在 MySQL 表达，已忽略");
            }
            else
            {
                // MySQL 8 表达式默认值必须包在括号里（CURRENT_TIMESTAMP 等关键字例外）
                var needsParens = emitted.Contains('(') && !emitted.StartsWith("CURRENT_", StringComparison.OrdinalIgnoreCase);
                sb.Append(needsParens ? $" DEFAULT ({emitted})" : $" DEFAULT {emitted}");
            }
        }
        if (col.IsAutoIncrement)
        {
            if (t.PrimaryKeyColumns.Contains(col.Name, StringComparer.OrdinalIgnoreCase))
                sb.Append(" AUTO_INCREMENT");
            else
                warnings.Add($"列 {col.Name}: AUTO_INCREMENT 列必须是主键，已忽略自增属性");
        }
        if (col.Comment is { } cm) sb.Append(" COMMENT '").Append(cm.Replace("'", "''")).Append('\'');
        return sb.ToString();
    }

    public IReadOnlyList<string> AlterTable(TableDiff diff, List<string> warnings)
    {
        var statements = new List<string>();
        var table = TableName(diff.Target);

        foreach (var col in diff.AddedColumns)
            statements.Add($"ALTER TABLE {table} ADD COLUMN {ColumnDef(col, diff.After, warnings)};");

        foreach (var col in diff.DroppedColumns)
            statements.Add($"ALTER TABLE {table} DROP COLUMN {Quote(col.Name)};");

        foreach (var change in diff.AlteredColumns)
        {
            var before = change.Before;
            var after = change.After;
            var typeChanged = !Equals(before.Type, after.Type);
            var nullChanged = before.Nullable != after.Nullable;
            var defaultChanged = !string.Equals(before.DefaultValueSql, after.DefaultValueSql, StringComparison.OrdinalIgnoreCase);
            if (!typeChanged && !nullChanged && !defaultChanged && before.IsAutoIncrement == after.IsAutoIncrement)
                continue;
            statements.Add($"ALTER TABLE {table} MODIFY COLUMN {ColumnDef(after, diff.After, warnings)};");
            if (typeChanged)
                warnings.Add($"列 {after.Name}: 类型变更 {before.Type} → {after.Type} 可能截断/转换失败现有数据");
        }

        return statements;
    }

    public IReadOnlyList<string> CreateIndex(CanonicalIndex idx, TableRef t, CanonicalTable? table = null)
    {
        // TEXT/BLOB/JSON 列建索引必须指定前缀长度
        string ColWithPrefix(string col)
        {
            var def = table?.Columns.FirstOrDefault(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase));
            var needsPrefix = def is null || def.Type.Id is CanonicalTypeId.Text or CanonicalTypeId.Json or CanonicalTypeId.Binary;
            return needsPrefix ? $"{Quote(col)}(191)" : Quote(col);
        }
        var sql = $"CREATE {(idx.IsUnique ? "UNIQUE " : "")}INDEX {Quote(idx.Name)} ON {TableName(t)} " +
                  $"({string.Join(", ", idx.Columns.Select(ColWithPrefix))})";
        return [sql + ";"];
    }

    public IReadOnlyList<string> DropIndex(string indexName, TableRef t) =>
        [$"DROP INDEX {Quote(indexName)} ON {TableName(t)};"];

    public IReadOnlyList<string> DropTable(TableRef t) =>
        [$"DROP TABLE IF EXISTS {TableName(t)};"];

    public IReadOnlyList<string> CreateForeignKey(CanonicalForeignKey fk, TableRef t)
    {
        var sql = $"ALTER TABLE {TableName(t)} ADD CONSTRAINT {Quote(fk.Name)} FOREIGN KEY " +
                  $"({string.Join(", ", fk.Columns.Select(Quote))}) REFERENCES {QualifyRef(fk.ReferencedTable)} " +
                  $"({string.Join(", ", fk.ReferencedColumns.Select(Quote))})" +
                  (fk.OnDelete is { } od && od != "NO ACTION" ? $" ON DELETE {od}" : "") +
                  (fk.OnUpdate is { } ou && ou != "NO ACTION" ? $" ON UPDATE {ou}" : "");
        return [sql + ";"];
    }

    public IReadOnlyList<string> DropForeignKey(string fkName, TableRef t) =>
        [$"ALTER TABLE {TableName(t)} DROP FOREIGN KEY {Quote(fkName)};"];

    public IReadOnlyList<string> TruncateTable(TableRef t) => [$"TRUNCATE TABLE {TableName(t)};"];

    public IReadOnlyList<string> ResetAutoIncrement(TableRef t, string pkColumn, long value) =>
        [$"ALTER TABLE {TableName(t)} AUTO_INCREMENT = {value};"];

    /// <summary>引用表可能带 schema/库前缀（如 ark_sync.users）：分段引号，避免整体引成一个标识符。</summary>
    private string QualifyRef(string referenced)
    {
        var parts = referenced.Split('.');
        return string.Join(".", parts.Select(Quote));
    }
}
