using System.Text;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Providers.DuckDB;

/// <summary>DuckDB DDL 生成器：主键 / UNIQUE / 具名外键内联建表，支持 ALTER COLUMN … TYPE。</summary>
public sealed class DuckDbDdlGenerator : IDdlGenerator
{
    public ArkDialect Dialect => ArkDialect.DuckDB;

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string TableName(TableRef t) => Quote(t.Table);

    public IReadOnlyList<string> CreateTable(TableRef target, CanonicalTable t, List<string> warnings)
    {
        // 文件库无 database 限定，忽略 target.Database
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
        {
            var def = new StringBuilder();
            def.Append("    ").Append(Quote(col.Name)).Append(' ');

            var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.DuckDB);
            warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
            def.Append(mapping.Sql);

            if (col.IsGenerated)
            {
                if (col.GeneratedExpression is { } expr)
                    def.Append(" AS (").Append(expr).Append(')');
                else
                    warnings.Add($"列 {col.Name}: 生成列缺少表达式，已按普通列创建");
            }
            else
            {
                if (!col.Nullable) def.Append(" NOT NULL");
                if (col.DefaultKind != ValueExprKind.None && col.DefaultValueSql is { } d)
                {
                    var emitted = FunctionTranslator.Emit(ArkDialect.DuckDB, col.DefaultKind, d);
                    if (emitted is null)
                        warnings.Add($"列 {col.Name}: 默认值 {d} 无法在 DuckDB 表达，已忽略");
                    else
                        def.Append(" DEFAULT ").Append(emitted);
                }
            }
            if (col.IsAutoIncrement)
                warnings.Add($"列 {col.Name}: DuckDB 无 AUTOINCREMENT，自增属性已忽略（数据按显式值复制）");
            defs.Add(def.ToString());
        }

        if (t.PrimaryKeyColumns.Count > 0)
            defs.Add("    PRIMARY KEY (" + string.Join(", ", t.PrimaryKeyColumns.Select(Quote)) + ")");

        foreach (var fk in t.ForeignKeys)
        {
            // SQLite 的隐式引用列会被读作 "rowid"（rowid 别名主键），DuckDB 表没有该列，引用会执行报错 → 跳过并提示
            if (fk.ReferencedColumns.Any(c => string.Equals(c, "rowid", StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"外键 {fk.Name}: 引用列为源库隐式 rowid（SQLite），目标表无此列，该外键跳过未创建（→ {fk.ReferencedTable}）");
                continue;
            }
            // 嵌入式单文件库：跨方言引用可能带 "db.table" 前缀，取末段作为本地表名
            var refName = fk.ReferencedTable.Split('.').Last().Trim('"', '`');
            var actions = FkActionSuffix(fk, warnings);
            defs.Add("    CONSTRAINT " + Quote(string.IsNullOrEmpty(fk.Name) ? $"fk_{t.Name}_{fk.Columns.FirstOrDefault()}" : fk.Name) +
                     " FOREIGN KEY (" + string.Join(", ", fk.Columns.Select(Quote)) + ") REFERENCES " +
                     Quote(refName) + " (" + string.Join(", ", fk.ReferencedColumns.Select(Quote)) + ")" +
                     actions);
        }

        sb.AppendLine(string.Join(",\n", defs));
        sb.Append(");");
        return [sb.ToString()];
    }

    /// <summary>DuckDB 外键不支持 ON DELETE/ON UPDATE 引用动作（仅 NO ACTION），声明了动作则剥离并提示。</summary>
    private string FkActionSuffix(CanonicalForeignKey fk, List<string> warnings)
    {
        if (fk.OnDelete is null && fk.OnUpdate is null) return "";
        warnings.Add($"外键 {fk.Name}: DuckDB 不支持 ON DELETE/ON UPDATE 引用动作（{fk.OnDelete ?? fk.OnUpdate}），已按 NO ACTION 创建");
        return "";
    }

    public IReadOnlyList<string> AlterTable(TableDiff diff, List<string> warnings)
    {
        var statements = new List<string>();
        var table = TableName(diff.Target);

        foreach (var col in diff.AddedColumns)
        {
            var def = ColumnDefForAdd(col, warnings);
            statements.Add($"ALTER TABLE {table} ADD COLUMN {def}");
        }

        foreach (var col in diff.DroppedColumns)
        {
            statements.Add($"ALTER TABLE {table} DROP COLUMN {Quote(col.Name)}");
        }

        foreach (var change in diff.AlteredColumns)
        {
            var before = change.Before;
            var after = change.After;
            if (before.Type != after.Type)
            {
                var mapping = TypeMapper.FromCanonical(after.Type, ArkDialect.DuckDB);
                warnings.AddRange(mapping.Warnings.Select(w => $"列 {after.Name}: {w}"));
                statements.Add($"ALTER TABLE {table} ALTER COLUMN {Quote(after.Name)} TYPE {mapping.Sql}");
            }
            if (before.Nullable != after.Nullable)
                warnings.Add($"列 {after.Name}: 暂不支持修改 NOT NULL 约束，已忽略");
            if (before.DefaultValueSql != after.DefaultValueSql && after.DefaultValueSql is not null)
                warnings.Add($"列 {after.Name}: 暂不支持修改列默认值，已忽略");
        }

        if (diff.PrimaryKeyChanged)
            warnings.Add("DuckDB 不支持直接修改主键，如需变更请重建表（v1 未实现）");

        return statements;
    }

    private string ColumnDefForAdd(CanonicalColumn col, List<string> warnings)
    {
        var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.DuckDB);
        warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
        var sb = new StringBuilder(Quote(col.Name)).Append(' ').Append(mapping.Sql);
        if (col.IsGenerated)
        {
            if (col.GeneratedExpression is { } expr) return sb.Append(" AS (").Append(expr).Append(')').ToString();
            warnings.Add($"列 {col.Name}: 生成列缺少表达式，已按普通列添加");
        }
        if (!col.Nullable)
        {
            if (col.DefaultValueSql is { } d)
            {
                var emitted = FunctionTranslator.Emit(ArkDialect.DuckDB, col.DefaultKind, d);
                if (emitted is null)
                {
                    warnings.Add($"列 {col.Name}: 默认值无法翻译，已按可空列添加");
                }
                else
                {
                    sb.Append(" NOT NULL DEFAULT ").Append(emitted);
                    return sb.ToString();
                }
            }
            else
            {
                warnings.Add($"列 {col.Name}: ADD COLUMN 不允许无默认值的 NOT NULL 列，已按可空列添加");
            }
        }
        return sb.ToString();
    }

    public IReadOnlyList<string> CreateIndex(CanonicalIndex idx, TableRef t, CanonicalTable? table = null)
    {
        var sql = $"CREATE {(idx.IsUnique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {Quote(idx.Name)} ON {TableName(t)} " +
                  $"({string.Join(", ", idx.Columns.Select(Quote))})";
        return [sql];
    }

    public IReadOnlyList<string> DropIndex(string indexName, TableRef t) =>
        [$"DROP INDEX IF EXISTS {Quote(indexName)};"];

    public IReadOnlyList<string> DropTable(TableRef t) =>
        [$"DROP TABLE IF EXISTS {TableName(t)};"];

    public IReadOnlyList<string> CreateForeignKey(CanonicalForeignKey fk, TableRef t)
    {
        // DuckDB 无法对既有表追加外键：建表时内联（InlineForeignKeys），差异路径返回空
        return [];
    }

    public IReadOnlyList<string> DropForeignKey(string fkName, TableRef t) => [];

    public IReadOnlyList<string> TruncateTable(TableRef t) => [$"TRUNCATE TABLE {TableName(t)}"];

    public IReadOnlyList<string> ResetAutoIncrement(TableRef t, string pkColumn, long value) => [];
}
