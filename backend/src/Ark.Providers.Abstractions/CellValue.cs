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

    /// <summary>
    /// 读取行后规范化并展平 Provider 专属值类型（如 ClickHouseDecimal / MySqlDecimal）为 CLR 原生
    /// （decimal / string），保证跨驱动参数绑定与行哈希稳定。跨方言同步的读取端必须使用本重载。
    /// </summary>
    public static void NormalizeRow(object?[] row, ArkDialect dialect)
    {
        NormalizeRow(row);
        for (var i = 0; i < row.Length; i++) row[i] = ToPortable(row[i]);
    }

    private static object? ToPortable(object? v)
    {
        if (v is null) return null;
        // 核心 CLR 类型（含 byte[]/DateTime/TimeSpan/DateOnly/Guid/数值/bool 等）直接放行
        if (v.GetType().Assembly == typeof(object).Assembly) return v;
        if (v is IConvertible)
        {
            try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
            catch { /* 数值转换失败退化为字符串 */ }
        }
        return v.ToString();
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
        // PG 系（含金仓/openGauss 的 Npgsql 系驱动）走原生 uuid 参数，其余以字符串传递
        Guid g when dialect is not (ArkDialect.PostgreSQL or ArkDialect.KingbaseES or ArkDialect.OpenGauss) => g.ToString(),
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
