using System.Security.Cryptography;
using System.Text;
using Ark.Providers.Abstractions;

namespace Ark.Sync;

/// <summary>
/// 行级 Diff 的应用层哈希与主键编码。
/// 哈希在三库之间保持一致：列名小写排序 + 值规范化（CellValue.CanonicalString），不依赖数据库 hash 函数。
/// </summary>
public static class RowHash
{
    public static string Compute(IReadOnlyList<string> columns, IReadOnlyList<object?> values)
    {
        var pairs = new (string Col, string Val)[columns.Count];
        for (var i = 0; i < columns.Count; i++)
            pairs[i] = (columns[i].ToLowerInvariant(), CellValue.CanonicalString(values[i]));
        Array.Sort(pairs, (x, y) => string.CompareOrdinal(x.Col, y.Col));

        var sb = new StringBuilder();
        foreach (var (col, val) in pairs)
            sb.Append(col).Append('=').Append(val).Append('\x1E');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes);
    }

    public static string ComputeRow(IReadOnlyList<string> allColumns, object?[] row, IReadOnlyList<string> hashColumns)
    {
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < allColumns.Count; i++) idx[allColumns[i]] = i;
        var cols = new List<string>();
        var vals = new List<object?>();
        foreach (var c in hashColumns)
        {
            cols.Add(c);
            vals.Add(idx.TryGetValue(c, out var i) ? row[i] : null);
        }
        return Compute(cols, vals);
    }

    /// <summary>复合主键编码（用于两侧对齐比较）。</summary>
    public static string PkKey(IReadOnlyList<string> pkColumns, object?[] row, IReadOnlyList<string> allColumns)
    {
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < allColumns.Count; i++) idx[allColumns[i]] = i;
        var sb = new StringBuilder();
        foreach (var pk in pkColumns)
        {
            sb.Append(CellValue.CanonicalString(idx.TryGetValue(pk, out var i) ? row[i] : null));
            sb.Append('\x1F');
        }
        return sb.ToString();
    }
}
