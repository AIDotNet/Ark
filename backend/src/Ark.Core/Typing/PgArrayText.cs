using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ark.Core.Typing;

/// <summary>
/// PostgreSQL 数组文本格式（{1,2,3} 数组字面量）与 CLR 数组 / JSON 值的互转。
/// 数组列在规范类型模型中降级为 Json（TypeMapper.PgToCanonical），若不经此处理：
/// 导出端退化为 "System.Int16[]" 之类的类型名，写入端则被 cast 成 jsonb 报 42804。
/// 导出（CSV / SQL dump / JSON / COPY）与导入取值均须经由此类。
/// </summary>
public static class PgArrayText
{
    /// <summary>原始类型名是否为数组："integer[]"（format_type 形态）或 "_text"（内部名形态）。</summary>
    public static bool IsArrayType(string? nativeType)
    {
        if (string.IsNullOrWhiteSpace(nativeType)) return false;
        var t = nativeType.Trim();
        return t.EndsWith("[]") || t.StartsWith('_');
    }

    /// <summary>CLR 数组（Npgsql 读回的 short[]/string[] 等）→ PG 数组文本；非数组原样转字符串。</summary>
    public static string FromObject(object? value) => value switch
    {
        Array a => FromEnumerable(a.Cast<object?>()),
        null or DBNull => "",
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// 导入取值：JSON 数组 → PG 数组文本；字符串形如 "[..]"（JSON 数组文本）→ 转换；
    /// 其余字符串原样返回（视为已是 {..} 数组文本，CSV 往返路径）。
    /// </summary>
    public static string FromJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Array => FromEnumerable(el.EnumerateArray().Select(JsonItem)),
        JsonValueKind.String => FromArrayishText(el.GetString()),
        _ => el.GetRawText(),
    };

    /// <summary>元素序列 → PG 数组文本：含特殊字符的元素双引号包裹并反斜杠转义，null 元素输出 NULL。</summary>
    public static string FromEnumerable(IEnumerable<object?> items)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(ElementText(item));
        }
        return sb.Append('}').ToString();
    }

    private static object? JsonItem(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        JsonValueKind.Array => e.EnumerateArray().Select(JsonItem).ToArray(), // 嵌套数组走语法级嵌套
        _ => e.GetRawText(), // 数字保持数字形态；嵌套对象按文本转义
    };

    /// <summary>字符串值：形如 JSON 数组（[..]）则转换；非法 JSON 原样保留，交由数据库报错。</summary>
    private static string FromArrayishText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        var t = s.Trim();
        if (!t.StartsWith('[')) return s;
        try
        {
            using var doc = JsonDocument.Parse(t);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? FromEnumerable(doc.RootElement.EnumerateArray().Select(JsonItem))
                : s;
        }
        catch (JsonException)
        {
            return s;
        }
    }

    private static string ElementText(object? item)
    {
        if (item is null or DBNull) return "NULL";
        if (item is Array inner) return FromEnumerable(inner.Cast<object?>()); // 嵌套/多维：语法级嵌套 {{1,2},3}，不加引号
        var s = item switch
        {
            bool b => b ? "true" : "false",
            _ => Convert.ToString(item, CultureInfo.InvariantCulture) ?? "",
        };
        return NeedsQuote(s) ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : s;
    }

    /// <summary>PG 数组解析器要求：空串、NULL 字样、含逗号/引号/花括号/反斜杠/空白的元素必须加引号。</summary>
    private static bool NeedsQuote(string s) =>
        s.Length == 0
        || string.Equals(s, "NULL", StringComparison.OrdinalIgnoreCase)
        || s.IndexOfAny(['\\', '"', '{', '}', ',']) >= 0
        || s.Any(char.IsWhiteSpace);
}
