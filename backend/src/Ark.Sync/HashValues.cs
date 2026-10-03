using System.Globalization;
using Ark.Core.Metadata;

namespace Ark.Sync;

/// <summary>
/// 行哈希前的跨方言值归一：同一语义在不同方言/驱动下的 CLR 表示可能不同
/// （SQLite 文本时间戳 vs PG DateTime；double 100.5 vs decimal 100.50；文本 UUID vs Guid）。
/// Diff 比对与抽样校验必须按"目标列的规范类型"归一两侧后再哈希，否则会产生永动更新/误报。
/// </summary>
public static class HashValues
{
    public static object? Normalize(object? value, CanonicalTypeId typeId)
    {
        if (value is null or DBNull) return null;
        switch (typeId)
        {
            case CanonicalTypeId.DateTime:
            case CanonicalTypeId.DateTimeOffset:
                return NormalizeDateTime(value);
            case CanonicalTypeId.Date:
                var dt = AsDateTime(value);
                return dt?.Date;
            case CanonicalTypeId.Time:
                if (value is string ts && TimeSpan.TryParse(ts, CultureInfo.InvariantCulture, out var t)) return t.ToString();
                return value;
            case CanonicalTypeId.Guid:
                return value switch
                {
                    Guid g => "g:" + g.ToString("D"),
                    string gs when System.Guid.TryParse(gs, out var parsed) => "g:" + parsed.ToString("D"),
                    _ => value,
                };
            case CanonicalTypeId.Decimal:
                return NormalizeDecimal(value);
            case CanonicalTypeId.Boolean:
                return value switch
                {
                    bool b => b ? "1" : "0",
                    long l => l == 0 ? "0" : "1",
                    int i => i == 0 ? "0" : "1",
                    short sh => sh == 0 ? "0" : "1",
                    string bs when bool.TryParse(bs, out var pb) => pb ? "1" : "0",
                    _ => value,
                };
            case CanonicalTypeId.Single or CanonicalTypeId.Double:
                if (value is double d) return d.ToString("G17", CultureInfo.InvariantCulture);
                if (value is float f) return f.ToString("G9", CultureInfo.InvariantCulture);
                if (value is string ds && double.TryParse(ds, NumberStyles.Any, CultureInfo.InvariantCulture, out var pd)) return pd.ToString("G17", CultureInfo.InvariantCulture);
                return value;
            default:
                return value;
        }
    }

    /// <summary>按目标列类型序列归一一行（返回新数组，不改原行）。</summary>
    public static object?[] NormalizeRow(object?[] row, CanonicalTypeId?[] types)
    {
        var copy = new object?[row.Length];
        for (var i = 0; i < row.Length; i++)
            copy[i] = i < types.Length && types[i] is { } t ? Normalize(row[i], t) : row[i];
        return copy;
    }

    private static object? NormalizeDateTime(object? v)
    {
        var dt = AsDateTime(v);
        if (dt is null) return v is string s ? "s:" + s : v;
        var universal = dt.Value.Kind == DateTimeKind.Unspecified
            ? System.DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc).ToUniversalTime()
            : dt.Value.ToUniversalTime();
        return universal.ToString("O", CultureInfo.InvariantCulture);
    }

    private static System.DateTime? AsDateTime(object? v) => v switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        string s => System.DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null,
        _ => null,
    };

    /// <summary>decimal 双侧统一去掉无效尾零：20.00 / 20.0 / 20 → "20"；100.50 / 100.5 → "100.5"。</summary>
    private static object? NormalizeDecimal(object? v)
    {
        decimal? d = v switch
        {
            decimal m => m,
            double db => (decimal)db,
            float f => (decimal)f,
            long l => (decimal)l,
            int i => (decimal)i,
            string s => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : null,
            _ => null,
        };
        if (d is null) return v;
        var text = d.Value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.'))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }
        return text;
    }
}
