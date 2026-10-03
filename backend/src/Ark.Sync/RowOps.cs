using System.Data.Common;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

/// <summary>
/// 主键键编码与 keyset 谓词：
/// - 键编码沿用 RowHash.PkKey 的规范化字符串（值以 \x1F 连接），仅用于内存中的集合匹配与断点存储；
/// - 排序/范围一律交由数据库（ORDER BY / (a,b) &gt; (@..)），内存窗口内用字典匹配，不依赖编码顺序。
/// </summary>
public static class KeyCodec
{
    public const char Sep = '\x1F';
    public const string NullSentinel = "\x01NULL";

    /// <summary>行 → 键编码（按 pkCols 从行中取值）。</summary>
    public static string Encode(IReadOnlyList<string> pkCols, object?[] row, IReadOnlyList<string> allCols) =>
        RowHash.PkKey(pkCols, row, allCols);

    public static string EncodeValues(IReadOnlyList<object?> pkValues)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in pkValues)
        {
            sb.Append(CellValue.CanonicalString(v));
            sb.Append(Sep);
        }
        return sb.ToString();
    }

    /// <summary>键编码 → CLR 值数组（s:字符串 / 数值 / NULL）。编码以 Sep 结尾，末段恒为空。</summary>
    public static object?[] Decode(string key)
    {
        var parts = key.Split(Sep);
        var count = Math.Max(0, parts.Length - 1); // 末尾分隔符产生的空段
        var values = new object?[count];
        for (var i = 0; i < count; i++) values[i] = DecodeValue(parts[i]);
        return values;
    }

    public static object? DecodeValue(string s)
    {
        if (s == NullSentinel) return null;
        if (s.StartsWith("s:", StringComparison.Ordinal)) return s[2..];
        if (s == "1" || s == "0") return long.TryParse(s, out var b) ? b : s;
        return long.TryParse(s, out var l) ? l : s;
    }

    /// <summary>keyset 下界谓词：pk 元组 &gt; afterKey。返回 SQL 片段（参数已加入 cmd）。</summary>
    public static string KeysetAfter(IDbProvider p, IReadOnlyList<string> pkCols, string? afterKey, DbCommand cmd)
    {
        if (afterKey is null) return "";
        return RangePredicate(p, pkCols, Decode(afterKey), ">", cmd, "@ka");
    }

    /// <summary>keyset 上界谓词：pk 元组 &lt;= upperKey。</summary>
    public static string KeysetUpper(IDbProvider p, IReadOnlyList<string> pkCols, string upperKey, DbCommand cmd) =>
        RangePredicate(p, pkCols, Decode(upperKey), "<=", cmd, "@ku");

    /// <summary>(a,b) &gt; (@a,@b) 形式的行值比较；单列退化为普通比较。</summary>
    public static string RangePredicate(
        IDbProvider p, IReadOnlyList<string> pkCols, object?[] values, string op, DbCommand cmd, string prefix = "@ks")
    {
        if (pkCols.Count == 1)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = $"{prefix}0";
            param.Value = CellValue.ToParam(values[0], p.Dialect);
            cmd.Parameters.Add(param);
            return $"{p.Ddl.Quote(pkCols[0])} {op} {prefix}0";
        }
        var ps = new List<string>();
        for (var i = 0; i < pkCols.Count; i++)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = $"{prefix}{i}";
            param.Value = CellValue.ToParam(values[i], p.Dialect);
            cmd.Parameters.Add(param);
            ps.Add(param.ParameterName);
        }
        return $"({string.Join(", ", pkCols.Select(p.Ddl.Quote))}) {op} ({string.Join(", ", ps)})";
    }
}

/// <summary>
/// 行级写操作共享实现（全量复制与分块 Diff 共用）：
/// 批量插入（冲突语义/PG cast）、按主键更新、分块删除。全部走参数化 SQL。
/// </summary>
public static class RowOps
{
    /// <summary>各方言多值 INSERT 的参数上限（预留安全余量）。</summary>
    public static int MaxRowsPerBatch(ArkDialect dialect, int colCount)
    {
        var byDialect = dialect switch
        {
            ArkDialect.MySQL => 65000 / Math.Max(1, colCount),
            ArkDialect.SQLite => 900 / Math.Max(1, colCount), // Microsoft.Data.Sqlite 兼容旧上限
            _ => 65000 / Math.Max(1, colCount),
        };
        return Math.Max(1, byDialect);
    }

    /// <summary>批量插入；tx 为空时自建事务并提交。conflict=SkipExisting 时加 ON CONFLICT / INSERT IGNORE。</summary>
    public static async Task<long> InsertRowsAsync(
        DbConnection conn, DbTransaction? tx, IDbProvider target, TableRef tgtRef,
        IReadOnlyList<string> colNames, IReadOnlyList<CanonicalColumn> targetCols,
        List<object?[]> rows, ConflictMode conflict, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var max = MaxRowsPerBatch(target.Dialect, colNames.Count);
        long total = 0;
        for (var offset = 0; offset < rows.Count; offset += max)
        {
            var slice = rows.Skip(offset).Take(max).ToList();
            await using var cmd = conn.CreateCommand();
            if (tx is not null) cmd.Transaction = tx;
            var colSql = string.Join(", ", colNames.Select(target.Ddl.Quote));
            var groups = new List<string>(slice.Count);
            var pi = 0;
            foreach (var row in slice)
            {
                var ps = new List<string>();
                for (var c = 0; c < colNames.Count; c++)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = $"@p{pi++}";
                    p.Value = CellValue.ToParam(row[c], target.Dialect);
                    cmd.Parameters.Add(p);
                    ps.Add(WithPgCast(target, p.ParameterName, targetCols, colNames[c]));
                }
                groups.Add("(" + string.Join(", ", ps) + ")");
            }
            var conflictSql = conflict switch
            {
                ConflictMode.SkipExisting when target.Dialect == ArkDialect.MySQL => " IGNORE",
                ConflictMode.SkipExisting => " ON CONFLICT DO NOTHING",
                _ => "",
            };
            cmd.CommandText =
                $"INSERT INTO {target.Ddl.TableName(tgtRef)} ({colSql}) VALUES {string.Join(", ", groups)}{conflictSql}";
            total += await cmd.ExecuteNonQueryAsync(ct);
        }
        return total;
    }

    /// <summary>按主键更新一行（仅更新非主键列）。</summary>
    public static async Task<long> UpdateRowAsync(
        DbConnection conn, DbTransaction tx, IDbProvider target, TableRef t,
        IReadOnlyList<string> pkCols, IReadOnlyList<string> colNames, IReadOnlyList<CanonicalColumn> targetCols,
        object?[] row, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        var sets = new List<string>();
        var pi = 0;
        for (var c = 0; c < colNames.Count; c++)
        {
            var col = colNames[c];
            if (pkCols.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;
            var p = cmd.CreateParameter();
            p.ParameterName = $"@u{pi++}";
            p.Value = CellValue.ToParam(row[c], target.Dialect);
            cmd.Parameters.Add(p);
            sets.Add($"{target.Ddl.Quote(col)} = {WithPgCast(target, p.ParameterName, targetCols, col)}");
        }
        if (sets.Count == 0) return 0;

        var whereParts = new List<string>();
        foreach (var pk in pkCols)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = $"@w{whereParts.Count}";
            var rowIdx = -1;
            for (var i = 0; i < colNames.Count; i++)
            {
                if (string.Equals(colNames[i], pk, StringComparison.OrdinalIgnoreCase)) { rowIdx = i; break; }
            }
            p.Value = CellValue.ToParam(rowIdx >= 0 ? row[rowIdx] : null, target.Dialect);
            cmd.Parameters.Add(p);
            whereParts.Add($"{target.Ddl.Quote(pk)} = {p.ParameterName}");
        }
        cmd.CommandText =
            $"UPDATE {target.Ddl.TableName(t)} SET {string.Join(", ", sets)} WHERE {string.Join(" AND ", whereParts)}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>按键列表删除（IN / OR 组合，参数化）。</summary>
    public static async Task<long> DeleteRowsAsync(
        DbConnection conn, DbTransaction tx, IDbProvider target, TableRef t,
        IReadOnlyList<string> pkCols, List<object?[]> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return 0;
        var chunkSize = Math.Max(1, 900 / Math.Max(1, pkCols.Count));
        long total = 0;
        for (var offset = 0; offset < keys.Count; offset += chunkSize)
        {
            var chunk = keys.Skip(offset).Take(chunkSize).ToList();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var where = BuildKeyInPredicate(target, pkCols, chunk, cmd);
            cmd.CommandText = $"DELETE FROM {target.Ddl.TableName(t)} WHERE {where}";
            total += await cmd.ExecuteNonQueryAsync(ct);
        }
        return total;
    }

    /// <summary>主键 IN / OR 谓词（键值为 CLR 值）。</summary>
    public static string BuildKeyInPredicate(
        IDbProvider target, IReadOnlyList<string> pkCols, List<object?[]> keys, DbCommand cmd)
    {
        if (pkCols.Count == 1)
        {
            var ps = new List<string>();
            for (var i = 0; i < keys.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@k{i}";
                p.Value = CellValue.ToParam(keys[i][0], target.Dialect);
                cmd.Parameters.Add(p);
                ps.Add(p.ParameterName);
            }
            return $"{target.Ddl.Quote(pkCols[0])} IN ({string.Join(", ", ps)})";
        }
        var parts = new List<string>();
        for (var ki = 0; ki < keys.Count; ki++)
        {
            var ands = new List<string>();
            for (var c = 0; c < pkCols.Count; c++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@k{ki}_{c}";
                p.Value = CellValue.ToParam(keys[ki][c], target.Dialect);
                cmd.Parameters.Add(p);
                ands.Add($"{target.Ddl.Quote(pkCols[c])} = {p.ParameterName}");
            }
            parts.Add("(" + string.Join(" AND ", ands) + ")");
        }
        return string.Join(" OR ", parts);
    }

    /// <summary>PostgreSQL：参数按目标列类型显式 cast（jsonb/timestamp 等不接受未知文本参数）。</summary>
    public static string WithPgCast(
        IDbProvider target, string paramName, IReadOnlyList<CanonicalColumn> targetCols, string colName)
    {
        if (target.Dialect != ArkDialect.PostgreSQL) return paramName;
        var col = targetCols.FirstOrDefault(c => string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase));
        if (col is null || Ark.Core.Typing.TypeMapper.PgCastFor(col) is not { } cast) return paramName;
        return $"{paramName}::{cast}";
    }
}
