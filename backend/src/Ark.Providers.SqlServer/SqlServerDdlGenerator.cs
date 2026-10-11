using System.Text;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;

namespace Ark.Providers.SqlServer;

/// <summary>
/// SQL Server DDL 生成器：标识符用 [x] 转义（闭字符 ] 翻倍），NVARCHAR/NCHAR/datetime2/IDENTITY(1,1) 等 T-SQL 语法。
/// </summary>
public sealed class SqlServerDdlGenerator : IDdlGenerator
{
    public ArkDialect Dialect => ArkDialect.SqlServer;

    public string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public string TableName(TableRef t) => Quote(t.Schema ?? "dbo") + "." + Quote(t.Table);

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
        if (t.Comment is { })
            warnings.Add("表注释：SQL Server 需通过扩展属性（sp_addextendedproperty）维护，v1 不迁移");
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(Quote(t.Name)).AppendLine(" (");
        var defs = new List<string>();
        foreach (var col in t.Columns)
            defs.Add("    " + ColumnDef(col, t, warnings));
        if (t.PrimaryKeyColumns.Count > 0)
            defs.Add("    PRIMARY KEY (" + string.Join(", ", t.PrimaryKeyColumns.Select(Quote)) + ")");
        sb.AppendLine(string.Join(",\n", defs));
        sb.Append(')');
        return [sb.ToString() + ";"];
    }

    /// <summary>完整列定义（建表/加列共用）。生成列输出 [col] AS (expr)；自增列输出 IDENTITY(1,1)。</summary>
    private string ColumnDef(CanonicalColumn col, CanonicalTable t, List<string> warnings)
    {
        if (col.IsGenerated)
        {
            if (col.GeneratedExpression is { } expr)
                return $"{Quote(col.Name)} AS ({expr})";
            warnings.Add($"列 {col.Name}: 生成列缺少表达式，已按普通列创建");
        }

        var mapping = TypeMapper.FromCanonical(col.Type, ArkDialect.SqlServer);
        warnings.AddRange(mapping.Warnings.Select(w => $"列 {col.Name}: {w}"));
        var sb = new StringBuilder(Quote(col.Name)).Append(' ').Append(mapping.Sql);

        if (col.IsAutoIncrement)
        {
            if (col.Type.Id is CanonicalTypeId.Int16 or CanonicalTypeId.Int32 or CanonicalTypeId.Int64)
                sb.Append(" IDENTITY(1,1)");
            else
                warnings.Add($"列 {col.Name}: 自增列类型 {col.Type} 非整数，已忽略 IDENTITY 属性");
        }

        // IDENTITY 列隐式 NOT NULL，显式写出亦合法；带默认值约束的列不能直接 ALTER 类型，建表时无此限制
        if (!col.Nullable) sb.Append(" NOT NULL");

        if (col.DefaultKind != ValueExprKind.None && col.DefaultValueSql is { } d && !col.IsAutoIncrement)
        {
            var emitted = FunctionTranslator.Emit(ArkDialect.SqlServer, col.DefaultKind, d);
            if (emitted is null)
                warnings.Add($"列 {col.Name}: 默认值 {d} 无法在 SQL Server 表达，已忽略");
            else
                sb.Append(" DEFAULT ").Append(emitted);
        }

        if (col.Comment is { })
            warnings.Add($"列 {col.Name}: SQL Server 列注释需通过扩展属性维护，v1 不迁移");
        return sb.ToString();
    }

    public IReadOnlyList<string> AlterTable(TableDiff diff, List<string> warnings)
    {
        var statements = new List<string>();
        var table = TableName(diff.Target);

        foreach (var col in diff.AddedColumns)
            statements.Add($"ALTER TABLE {table} ADD {ColumnDef(col, diff.After, warnings)};");

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

            // ALTER COLUMN 的硬限制：主键列 / IDENTITY 列不允许改；带默认值约束的列需先 DROP CONSTRAINT
            var isPk = diff.After.PrimaryKeyColumns.Contains(after.Name, StringComparer.OrdinalIgnoreCase);
            if (isPk || before.IsAutoIncrement != after.IsAutoIncrement)
            {
                warnings.Add($"列 {after.Name}: SQL Server 不允许直接 ALTER 主键列/IDENTITY 列，请人工处理");
                continue;
            }
            if (before.DefaultValueSql is not null)
                warnings.Add($"列 {after.Name}: 列带默认值约束，SQL Server 修改类型前需先删除约束（如执行失败请人工处理）");

            if (typeChanged || nullChanged)
            {
                var mapping = TypeMapper.FromCanonical(after.Type, ArkDialect.SqlServer);
                warnings.AddRange(mapping.Warnings.Select(w => $"列 {after.Name}: {w}"));
                statements.Add(
                    $"ALTER TABLE {table} ALTER COLUMN {Quote(after.Name)} {mapping.Sql} {(after.Nullable ? "NULL" : "NOT NULL")};");
                if (typeChanged)
                    warnings.Add($"列 {after.Name}: 类型变更 {before.Type} → {after.Type} 可能截断/转换失败现有数据");
            }

            if (defaultChanged)
                warnings.Add($"列 {after.Name}: SQL Server 默认值约束需具名管理（DROP/ADD CONSTRAINT），v1 不自动修改默认值");
        }

        if (diff.PrimaryKeyChanged)
            warnings.Add("SQL Server 主键变更涉及删除/重建约束与聚集索引，v1 请人工处理");

        return statements;
    }

    public IReadOnlyList<string> CreateIndex(CanonicalIndex idx, TableRef t, CanonicalTable? table = null)
    {
        var sql = $"CREATE {(idx.IsUnique ? "UNIQUE " : "")}INDEX {Quote(idx.Name)} ON {TableName(t)} " +
                  $"({string.Join(", ", idx.Columns.Select(Quote))})";
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
        [$"ALTER TABLE {TableName(t)} DROP CONSTRAINT {Quote(fkName)};"];

    public IReadOnlyList<string> TruncateTable(TableRef t) => [$"TRUNCATE TABLE {TableName(t)};"];

    public IReadOnlyList<string> ResetAutoIncrement(TableRef t, string pkColumn, long value) =>
        [$"DBCC CHECKIDENT ('{TableName(t)}', RESEED, {value});"];

    /// <summary>引用表可能带 schema/库前缀（如 dbo.users）：分段引号，避免整体引成一个标识符。</summary>
    private string QualifyRef(string referenced)
    {
        var parts = referenced.Split('.');
        return string.Join(".", parts.Select(Quote));
    }
}
