using System.Text;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Providers.SQLite;

public sealed class SqliteDdlGenerator : IDdlGenerator
{
    public ArkDialect Dialect => ArkDialect.SQLite;

    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string TableName(TableRef t) => Quote(t.Table);

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

        var singleIntegerPk = t.PrimaryKeyColumns.Count == 1 &&
                              t.Columns.FirstOrDefault(c => c.Name == t.PrimaryKeyColumns[0]) is { } pkCol &&
                              pkCol.Type.Id is CanonicalTypeId.Int16 or CanonicalTypeId.Int32 or CanonicalTypeId.Int64;

        foreach (var col in t.Columns)
        {
            var def = new StringBuilder();
            def.Append("    ").Append(Quote(col.Name)).Append(' ');

            if (col.IsAutoIncrement && singleIntegerPk && col.Name == t.PrimaryKeyColumns[0])
            {
                def.Append("INTEGER PRIMARY KEY AUTOINCREMENT");
            }
            else
            {
                var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.SQLite);
                warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
                def.Append(mapping.Sql);
                if (col.IsGenerated)
                {
                    if (col.GeneratedExpression is { } expr)
                        def.Append(" AS (").Append(expr).Append(") STORED");
                    else
                        warnings.Add($"列 {col.Name}: 生成列缺少表达式，已按普通列创建");
                }
                else
                {
                    if (!col.Nullable) def.Append(" NOT NULL");
                    if (col.DefaultKind != ValueExprKind.None && col.DefaultValueSql is { } d)
                    {
                        var emitted = FunctionTranslator.Emit(ArkDialect.SQLite, col.DefaultKind, d);
                        if (emitted is null)
                            warnings.Add($"列 {col.Name}: 默认值 {d} 无法在 SQLite 表达，已忽略");
                        else
                            def.Append(" DEFAULT ").Append(emitted);
                    }
                }
            }
            defs.Add(def.ToString());
        }

        if (!singleIntegerPk && t.PrimaryKeyColumns.Count > 0)
            defs.Add("    PRIMARY KEY (" + string.Join(", ", t.PrimaryKeyColumns.Select(Quote)) + ")");

        foreach (var fk in t.ForeignKeys)
        {
            // SQLite 单库：跨方言引用可能带 "db.table" 前缀，取末段作为本地表名
            var refName = fk.ReferencedTable.Split('.').Last().Trim('"', '`');
            defs.Add("    FOREIGN KEY (" + string.Join(", ", fk.Columns.Select(Quote)) + ") REFERENCES " +
                     Quote(refName) + " (" + string.Join(", ", fk.ReferencedColumns.Select(Quote)) + ")" +
                     (fk.OnDelete is { } od ? $" ON DELETE {od}" : "") +
                     (fk.OnUpdate is { } ou ? $" ON UPDATE {ou}" : ""));
        }

        sb.AppendLine(string.Join(",\n", defs));
        sb.Append(");");
        return [sb.ToString()];
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
                warnings.Add($"列 {after.Name}: SQLite 不支持修改列类型（{before.Type} → {after.Type}），如需变更请重建表（v1 未实现重建流程）");
            if (before.Nullable != after.Nullable)
                warnings.Add($"列 {after.Name}: SQLite 不支持修改 NOT NULL 约束，已忽略");
            if (before.DefaultValueSql != after.DefaultValueSql && after.DefaultValueSql is not null)
                warnings.Add($"列 {after.Name}: SQLite 不支持修改列默认值，已忽略");
        }

        if (diff.PrimaryKeyChanged)
            warnings.Add("SQLite 不支持修改主键，如需变更请重建表（v1 未实现）");

        return statements;
    }

    private string ColumnDefForAdd(CanonicalColumn col, List<string> warnings)
    {
        var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.SQLite);
        warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
        var sb = new StringBuilder(Quote(col.Name)).Append(' ').Append(mapping.Sql);
        if (!col.Nullable)
        {
            if (col.DefaultValueSql is { } d)
            {
                var emitted = FunctionTranslator.Emit(ArkDialect.SQLite, col.DefaultKind, d);
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
                warnings.Add($"列 {col.Name}: SQLite 的 ADD COLUMN 不允许无默认值的 NOT NULL 列，已按可空列添加");
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
        // SQLite 无法对既有表追加外键
        return [];
    }

    public IReadOnlyList<string> DropForeignKey(string fkName, TableRef t) => [];

    public IReadOnlyList<string> TruncateTable(TableRef t) => [$"DELETE FROM {TableName(t)}"];

    public IReadOnlyList<string> ResetAutoIncrement(TableRef t, string pkColumn, long value) => [];
}
