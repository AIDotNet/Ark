using System.Text;
using Ark.Core.Metadata;

namespace Ark.Core.Typing;

/// <summary>
/// 跨库“插件函数”翻译：仅作用于默认值表达式与生成列（数据本体不翻译）。
/// 识别源方言表达式的语义（当前时间戳/UUID/COALESCE/拼接…），再按目标方言输出。
/// 无法翻译时返回原表达式并由调用方（同步计划）给出警告。
/// </summary>
public static partial class FunctionTranslator
{
    public static ValueExprKind Recognize(ArkDialect source, string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return ValueExprKind.None;
        var e = StripOuter(expr.Trim()).ToLowerInvariant();

        // 标准关键字（三库通用）
        if (e == "current_timestamp" || RegexTimestampN().IsMatch(e)) return ValueExprKind.CurrentTimestamp;
        if (e == "current_date") return ValueExprKind.CurrentDate;
        if (e == "current_time") return ValueExprKind.CurrentTime;
        if (e == "localtimestamp" || e == "now()" || e == "localtimestamp()" ||
            e == "transaction_timestamp()" || e == "statement_timestamp()") return ValueExprKind.CurrentTimestamp;

        return source switch
        {
            ArkDialect.PostgreSQL => RecognizePg(e),
            ArkDialect.MySQL => RecognizeMy(e),
            ArkDialect.SQLite => RecognizeSq(e),
            _ => ValueExprKind.Unrecognized,
        };
    }

    private static ValueExprKind RecognizePg(string e)
    {
        if (e is "gen_random_uuid()" or "uuid_generate_v4()" or "uuid_generate_v1()") return ValueExprKind.UuidV4;
        if (e.StartsWith("coalesce(") || e.StartsWith("ifnull(") || e.StartsWith("nvl(")) return ValueExprKind.Coalesce;
        if (e.StartsWith("concat(")) return ValueExprKind.Concat;
        if (HasTopLevelConcat(e)) return ValueExprKind.Concat;
        return ClassifyCommon(e);
    }

    private static ValueExprKind RecognizeMy(string e)
    {
        if (e is "uuid()") return ValueExprKind.UuidV4;
        if (e is "curdate()" or "current_date()") return ValueExprKind.CurrentDate;
        if (e is "curtime()" or "current_time()") return ValueExprKind.CurrentTime;
        if (e is "now()" or "current_timestamp()" or "localtime()" or "localtime" or "sysdate()") return ValueExprKind.CurrentTimestamp;
        if (e.StartsWith("coalesce(") || e.StartsWith("ifnull(")) return ValueExprKind.Coalesce;
        if (e.StartsWith("concat(")) return ValueExprKind.Concat;
        return ClassifyCommon(e);
    }

    private static ValueExprKind RecognizeSq(string e)
    {
        if (e is "datetime('now','localtime')" or "datetime('now')") return ValueExprKind.CurrentTimestamp;
        if (e == "date('now')") return ValueExprKind.CurrentDate;
        if (e == "time('now')") return ValueExprKind.CurrentTime;
        if (e.StartsWith("strftime(") && e.EndsWith("'now')") && e.Contains("%y")) return ValueExprKind.CurrentTimestamp;
        if (e.StartsWith("coalesce(") || e.StartsWith("ifnull(")) return ValueExprKind.Coalesce;
        if (HasTopLevelConcat(e)) return ValueExprKind.Concat;
        return ClassifyCommon(e);
    }

    private static ValueExprKind ClassifyCommon(string e)
    {
        if (e.StartsWith("coalesce(")) return ValueExprKind.Coalesce;
        if (IsStringLiteral(e) || RegexNumber().IsMatch(e)) return ValueExprKind.Literal;
        return ValueExprKind.Unrecognized;
    }

    /// <summary>按目标方言输出默认值表达式；返回 null 表示目标方言不支持（如 SQLite 的 UUID）。</summary>
    public static string? Emit(ArkDialect target, ValueExprKind kind, string rawExpr) => kind switch
    {
        ValueExprKind.None => null,
        ValueExprKind.Literal or ValueExprKind.Unrecognized => StripOuter(rawExpr.Trim()),
        ValueExprKind.CurrentTimestamp => target switch
        {
            ArkDialect.PostgreSQL => "now()",
            ArkDialect.MySQL => "CURRENT_TIMESTAMP",
            _ => "CURRENT_TIMESTAMP",
        },
        ValueExprKind.CurrentDate => "CURRENT_DATE",
        ValueExprKind.CurrentTime => "CURRENT_TIME",
        ValueExprKind.UuidV4 => target switch
        {
            ArkDialect.PostgreSQL => "gen_random_uuid()",
            ArkDialect.MySQL => "UUID()",
            _ => null, // SQLite 无内置 UUID 函数 → 同步计划中报错提示
        },
        ValueExprKind.Coalesce => "COALESCE(" + string.Join(", ", SplitTopLevelArgs(ArgsOf(rawExpr))) + ")",
        ValueExprKind.Concat => ConcatFor(target, rawExpr),
        _ => StripOuter(rawExpr.Trim()),
    };

    /// <summary>拼接表达式翻译：CONCAT(a,b) 与 a || b 两种形态互转，操作数按顶层拆分（含一层递归展开）。</summary>
    private static string ConcatFor(ArkDialect target, string raw)
    {
        var inner = raw.Trim();
        List<string> operands;
        var lower = inner.ToLowerInvariant();
        if (lower.StartsWith("concat(") && inner.EndsWith(')'))
        {
            operands = SplitTopLevelArgs(ArgsOf(inner));
        }
        else
        {
            operands = SplitTopLevelConcat(inner);
        }
        // 递归展开一层嵌套的 concat/||
        operands = operands
            .SelectMany(op =>
            {
                var l = op.Trim().ToLowerInvariant();
                if (l.StartsWith("concat(") && op.Trim().EndsWith(')'))
                    return SplitTopLevelArgs(ArgsOf(op.Trim()));
                if (HasTopLevelConcat(op) && !op.Contains('('))
                    return SplitTopLevelConcat(op);
                return [op];
            })
            .ToList();
        return target == ArkDialect.MySQL
            ? "CONCAT(" + string.Join(", ", operands) + ")"
            : string.Join(" || ", operands);
    }

    /// <summary>按顶层 || 切分操作数（括号/字符串感知）。</summary>
    private static List<string> SplitTopLevelConcat(string s)
    {
        var result = new List<string>();
        var depth = 0;
        var inStr = false;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (inStr)
            {
                sb.Append(ch);
                if (ch == '\'') inStr = false;
                continue;
            }
            switch (ch)
            {
                case '\'': inStr = true; sb.Append(ch); break;
                case '(': depth++; sb.Append(ch); break;
                case ')': depth--; sb.Append(ch); break;
                case '|' when depth == 0 && i + 1 < s.Length && s[i + 1] == '|':
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                    i++; // 跳过第二个 |
                    break;
                default: sb.Append(ch); break;
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString().Trim());
        return result;
    }

    // ------------------------------ 工具 ------------------------------

    private static string ArgsOf(string expr)
    {
        var i = expr.IndexOf('(');
        var j = expr.LastIndexOf(')');
        return i >= 0 && j > i ? expr[(i + 1)..j] : expr;
    }

    /// <summary>按括号深度 0 处切分逗号参数。</summary>
    public static List<string> SplitTopLevelArgs(string s)
    {
        var result = new List<string>();
        var depth = 0;
        var inStr = false;
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (inStr)
            {
                sb.Append(ch);
                if (ch == '\'') inStr = false;
                continue;
            }
            switch (ch)
            {
                case '\'': inStr = true; sb.Append(ch); break;
                case '(': depth++; sb.Append(ch); break;
                case ')': depth--; sb.Append(ch); break;
                case ',' when depth == 0:
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                    break;
                default: sb.Append(ch); break;
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString().Trim());
        return result;
    }

    private static bool HasTopLevelConcat(string e)
    {
        var depth = 0;
        var inStr = false;
        for (var i = 0; i < e.Length; i++)
        {
            var ch = e[i];
            if (inStr) { if (ch == '\'') inStr = false; continue; }
            switch (ch)
            {
                case '\'': inStr = true; break;
                case '(': depth++; break;
                case ')': depth--; break;
                case '|' when depth == 0 && i + 1 < e.Length && e[i + 1] == '|': return true;
            }
        }
        return false;
    }

    private static bool IsStringLiteral(string e) =>
        e.StartsWith('\'') && e.EndsWith('\'') && e.Length >= 2;

    private static string StripOuter(string s)
    {
        var r = s.Trim();
        while (r.Length >= 2 && r.StartsWith('(') && r.EndsWith(')'))
        {
            var inner = r[1..^1].Trim();
            // 确保首尾括号配对（避免 (a)+(b) 被误剥）
            var depth = 0;
            var ok = true;
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '(') depth++;
                else if (inner[i] == ')') { depth--; if (depth < 0) { ok = false; break; } }
            }
            if (!ok || depth != 0) break;
            r = inner;
        }
        return r;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^current_timestamp\s*\(\s*\d*\s*\)$")]
    private static partial System.Text.RegularExpressions.Regex RegexTimestampN();

    [System.Text.RegularExpressions.GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial System.Text.RegularExpressions.Regex RegexNumber();
}
