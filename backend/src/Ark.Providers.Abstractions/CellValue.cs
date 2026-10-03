using System.Globalization;
using System.Text.Json;
using Ark.Core.Errors;
using Ark.Core.Metadata;

namespace Ark.Providers.Abstractions;

/// <summary>JSON 单元格 ↔ CLR 参数值的双向转换（按列的规范类型解释）。</summary>
public static class CellValue
{
    /// <summary>读取行后统一规范化：DBNull→null；pg "char"→string。</summary>
    public static void NormalizeRow(object?[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] == DBNull.Value) row[i] = null;
            else if (row[i] is char c) row[i] = c.ToString();
        }
    }

    public static object? FromJson(JsonElement el, CanonicalType type)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.True or JsonValueKind.False:
                return type.Id == CanonicalTypeId.Boolean ? el.GetBoolean() : (long)(el.GetBoolean() ? 1 : 0);
            case JsonValueKind.Number:
            {
                if (type.Id is CanonicalTypeId.Int16 or CanonicalTypeId.Int32 or CanonicalTypeId.Int64)
                    return el.GetInt64();
                if (type.Id == CanonicalTypeId.Decimal)
                    return el.GetDecimal();
                if (type.Id is CanonicalTypeId.Single or CanonicalTypeId.Double)
                    return el.GetDouble();
                // 数字到达字符串列：转文本
                return el.GetRawText();
            }
            case JsonValueKind.String:
            {
                var s = el.GetString();
                return type.Id switch
                {
                    CanonicalTypeId.Date or CanonicalTypeId.Time or CanonicalTypeId.DateTime or CanonicalTypeId.DateTimeOffset
                        => ParseDateTime(s),
                    CanonicalTypeId.Guid => Guid.TryParse(s, out var g) ? g : s,
                    CanonicalTypeId.Binary => ParseBinary(s),
                    _ => s,
                };
            }
            default:
                return el.GetRawText(); // 对象/数组 → JSON 文本（JSON 列）
        }
    }

    private static object? ParseDateTime(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            return dto.Offset == TimeSpan.Zero ? dto.UtcDateTime : dto;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return dt;
        return s; // 保留原文本，交由数据库报错
    }

    private static object? ParseBinary(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch { return System.Text.Encoding.UTF8.GetBytes(s); }
    }

    /// <summary>CLR 值 → ADO 参数值（按方言调整不兼容类型）。</summary>
    public static object? ToParam(object? value, ArkDialect dialect) => value switch
    {
        null => DBNull.Value,
        Guid g when dialect != ArkDialect.PostgreSQL => g.ToString(),
        bool b when dialect == ArkDialect.SQLite => b ? 1L : 0L,
        DateTimeOffset dto when dialect == ArkDialect.SQLite => dto.ToString("O"),
        _ => value,
    };

    /// <summary>行哈希用的规范化字符串（跨方言稳定：相同语义 → 相同字符串）。字符串加 s: 前缀，避免与数字/布尔碰撞。</summary>
    public static string CanonicalString(object? value) => value switch
    {
        null => "\x01NULL",
        string s => "s:" + s,
        bool b => b ? "1" : "0",
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Guid g => "g:" + g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => "s:" + value,
    };
}
