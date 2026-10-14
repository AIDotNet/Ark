using System.Text;
using Ark.Core.Metadata;

namespace Ark.Core.Typing;

/// <summary>
/// 跨库“插件函数”翻译：仅作用于默认值表达式与生成列（数据本体不翻译）。
/// 识别源方言表达式的语义（当前时间戳/UUID/COALESCE/拼接/布尔/数组与类型转换字面量…），再按目标方言输出。
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
            ArkDialect.MariaDB => RecognizeMy(e),
            ArkDialect.SqlServer => RecognizeMs(e),
            ArkDialect.ClickHouse => RecognizeCh(e),
            ArkDialect.DuckDB => RecognizeDk(e),
            ArkDialect.DM8 => RecognizeDm(e),
            ArkDialect.KingbaseES or ArkDialect.OpenGauss => RecognizePg(e),
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

    private static ValueExprKind RecognizeMs(string e)
    {
        if (e is "getdate()" or "getutcdate()" or "sysdatetime()" or "sysutcdatetime()" or "sysdatetimeoffset()")
            return ValueExprKind.CurrentTimestamp;
        if (e is "newid()" or "newsequentialid()") return ValueExprKind.UuidV4;
        if (e.StartsWith("isnull(") || e.StartsWith("coalesce(")) return ValueExprKind.Coalesce;
        if (HasTopLevelPlus(e)) return ValueExprKind.Concat;
        return ClassifyCommon(e);
    }

    private static ValueExprKind RecognizeCh(string e)
    {
        if (e == "now()" || e.StartsWith("now64(") || e.StartsWith("now(")) return ValueExprKind.CurrentTimestamp;
        if (e is "today()" or "currentdate()") return ValueExprKind.CurrentDate;
        if (e is "generateuuidv4()" or "generateuuidv4(1)" or "uuid()") return ValueExprKind.UuidV4;
        if (e.StartsWith("coalesce(") || e.StartsWith("ifnull(")) return ValueExprKind.Coalesce;
        if (e.StartsWith("concat(") || e.StartsWith("concatwithseparator(")) return ValueExprKind.Concat;
        if (HasTopLevelConcat(e)) return ValueExprKind.Concat;
        return ClassifyCommon(e);
    }

    private static ValueExprKind RecognizeDk(string e)
    {
        if (e is "uuid()" or "gen_random_uuid()" or "newid()") return ValueExprKind.UuidV4;
        if (e.StartsWith("coalesce(") || e.StartsWith("ifnull(") || e.StartsWith("nvl(")) return ValueExprKind.Coalesce;
        if (e.StartsWith("concat(")) return ValueExprKind.Concat;
        if (HasTopLevelConcat(e)) return ValueExprKind.Concat;
        return ClassifyCommon(e);
    }

    private static ValueExprKind RecognizeDm(string e)
    {
        if (e is "sysdate" or "sysdatetime()" or "systimestamp" || e.StartsWith("systimestamp("))
            return ValueExprKind.CurrentTimestamp;
        if (e is "sys_guid()" or "sysguid()") return ValueExprKind.UuidV4;
        if (e.StartsWith("nvl(") || e.StartsWith("coalesce(")) return ValueExprKind.Coalesce;
        if (HasTopLevelConcat(e)) return ValueExprKind.Concat;
        return ClassifyCommon(e);
    }

    private static ValueExprKind ClassifyCommon(string e)
    {
        if (e.StartsWith("coalesce(")) return ValueExprKind.Coalesce;
        if (e is "true" or "false") return ValueExprKind.BooleanLiteral;
        if (TryClassifyCastOrArray(e, out var castKind)) return castKind;
        if (IsStringLiteral(e) || RegexNumber().IsMatch(e)) return ValueExprKind.Literal;
        return ValueExprKind.Unrecognized;
    }

    /// <summary>按目标方言输出默认值表达式；返回 null 表示目标方言不支持（如 SQLite 的 UUID）。</summary>
    public static string? Emit(ArkDialect target, ValueExprKind kind, string rawExpr) => kind switch
    {
        ValueExprKind.None => null,
        ValueExprKind.Literal or ValueExprKind.Unrecognized => StripOuter(rawExpr.Trim()),
        ValueExprKind.BooleanLiteral => EmitBoolean(target, rawExpr),
        ValueExprKind.TypedLiteral => PgCastLiteralPart(rawExpr),
        ValueExprKind.ArrayLiteral => ArrayLiteralFor(target, rawExpr),
        ValueExprKind.CurrentTimestamp => target switch
        {
            ArkDialect.PostgreSQL => "now()",
            ArkDialect.SqlServer => "SYSDATETIME()",
            ArkDialect.ClickHouse => "now()",
            ArkDialect.DM8 => "SYSDATE",
            ArkDialect.DuckDB => "CURRENT_TIMESTAMP",
            ArkDialect.KingbaseES or ArkDialect.OpenGauss => "now()",
            _ => "CURRENT_TIMESTAMP",
        },
        ValueExprKind.CurrentDate => "CURRENT_DATE",
        ValueExprKind.CurrentTime => "CURRENT_TIME",
        ValueExprKind.UuidV4 => target switch
        {
            ArkDialect.PostgreSQL => "gen_random_uuid()",
            ArkDialect.MySQL => "UUID()",
            ArkDialect.MariaDB => "UUID()",
            ArkDialect.SqlServer => "NEWID()",
            ArkDialect.ClickHouse => "generateUUIDv4()",
            ArkDialect.DuckDB => "uuid()",
            ArkDialect.KingbaseES or ArkDialect.OpenGauss => "gen_random_uuid()",
            _ => null, // SQLite/DM8 无等效内置函数 → 同步计划中报错提示
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
        // 这些方言优先 CONCAT() 函数形式（SQL Server 无 ||；ClickHouse/DuckDB/MySQL 系语义一致）；
        // 其余（PG 系 / SQLite / DM8）用 ||
        return target is ArkDialect.MySQL or ArkDialect.MariaDB or ArkDialect.SqlServer
            or ArkDialect.ClickHouse or ArkDialect.DuckDB
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

    // ------------------------------ 布尔 / 类型转换字面量 / 数组 ------------------------------

    /// <summary>布尔字面量：SQL Server/SQLite/DM8 用 1/0（BIT/INTEGER 列），其余方言用 true/false。</summary>
    private static string EmitBoolean(ArkDialect target, string raw)
    {
        var isTrue = StripOuter(raw.Trim()).Equals("true", StringComparison.OrdinalIgnoreCase);
        return target is ArkDialect.SqlServer or ArkDialect.SQLite or ArkDialect.DM8
            ? (isTrue ? "1" : "0")
            : (isTrue ? "true" : "false");
    }

    /// <summary>剥离 '::type' 显式转换，仅保留字面量本体：目标列类型（可能已从枚举/自定义类型降级）决定实际类型。</summary>
    private static string PgCastLiteralPart(string raw) =>
        TrySplitPgCast(StripOuter(raw.Trim()), out var lit, out _) ? lit : StripOuter(raw.Trim());

    /// <summary>
    /// 数组字面量 → JSON 数组文本（SQL 字符串字面量形式）。规范模型中数组列一律降级为 Json/文本存储，
    /// 因此 '{}'::text[] 在任何目标端都应落为 '["a","b"]' 这类 JSON 默认值；
    /// 返回 null 表示表达式形态无法解析（交由调用方报错）。
    /// </summary>
    private static string? ArrayLiteralFor(ArkDialect target, string raw)
    {
        var json = ArrayToJson(StripOuter(raw.Trim()));
        if (json is null) return null;
        var quoted = SqlQuote(json);
        // MySQL/MariaDB 的 json/长文本列不接受裸字面量默认值，需以表达式形式 DEFAULT ('[]') 书写
        return target is ArkDialect.MySQL or ArkDialect.MariaDB ? $"({quoted})" : quoted;
    }

    private static string? ArrayToJson(string s)
    {
        string inner;
        var pgBrace = false;
        if (TrySplitPgCast(s, out var lit, out _))
        {
            var l = lit.ToLowerInvariant();
            if (l.StartsWith("array[") && lit.EndsWith(']'))
                inner = lit[6..^1];
            else if (lit.Length >= 2 && lit.StartsWith('\'') && lit.EndsWith('\''))
            {
                var body = lit[1..^1];
                if (body.StartsWith('{') && body.EndsWith('}')) { inner = body[1..^1]; pgBrace = true; }
                else if (body.StartsWith('[') && body.EndsWith(']')) inner = body[1..^1];
                else return null;
            }
            else if (l.StartsWith('[') && lit.EndsWith(']'))
                inner = lit[1..^1];
            else return null;
        }
        else if (s.StartsWith("array[", StringComparison.OrdinalIgnoreCase) && s.EndsWith(']'))
            inner = s[6..^1];
        else if (s.StartsWith('[') && s.EndsWith(']'))
            inner = s[1..^1];
        else return null;

        var elems = pgBrace ? SplitPgBraceElems(inner) : SplitTopLevelArgs(inner);
        return "[" + string.Join(",", elems.Select(e => ArrayElemToJson(e, pgBrace))) + "]";
    }

    /// <summary>识别带显式类型转换的字面量与数组字面量：'v'::type / 42::int / true::bool / '{}'::text[] / ARRAY[..]::int[] / [..]。</summary>
    private static bool TryClassifyCastOrArray(string e, out ValueExprKind kind)
    {
        kind = ValueExprKind.Unrecognized;
        var s = StripOuter(e);
        if (s.StartsWith('[') && s.EndsWith(']')) // 裸数组（DuckDB/ClickHouse 的列表默认值）
        {
            kind = ValueExprKind.ArrayLiteral;
            return true;
        }
        if (!TrySplitPgCast(s, out var lit, out var type)) return false;
        kind = IsArrayCast(lit, type) ? ValueExprKind.ArrayLiteral
            : lit.ToLowerInvariant() is "true" or "false" ? ValueExprKind.BooleanLiteral
            : ValueExprKind.TypedLiteral;
        return true;
    }

    /// <summary>在字符串/括号外找最右侧的 “::” 并拆分（'v'::type → ("'v'", "type")）；左右两侧均需形态合法。</summary>
    private static bool TrySplitPgCast(string s, out string literal, out string castType)
    {
        literal = castType = "";
        var idx = -1;
        var depth = 0;
        var inStr = false;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            var ch = s[i];
            if (inStr) { if (ch == '\'') inStr = false; continue; }
            if (ch == ':' && depth == 0 && i > 0 && s[i - 1] == ':') { idx = i - 1; break; }
            switch (ch)
            {
                case '\'': inStr = true; break;
                case ')': depth++; break;
                case '(': depth--; break;
            }
        }
        if (idx <= 0) return false;
        literal = s[..idx].Trim();
        castType = s[(idx + 2)..].Trim();
        return literal.Length > 0 && castType.Length > 0
            && IsLiteralPart(literal) && LooksLikeTypeName(castType);
    }

    /// <summary>“::” 左侧是否为可迁移的字面量本体：字符串 / 数字 / 布尔 / 数组。</summary>
    private static bool IsLiteralPart(string lit)
    {
        if (IsStringLiteral(lit) || RegexNumber().IsMatch(lit)) return true;
        var l = lit.ToLowerInvariant();
        return l is "true" or "false"
            || (l.StartsWith("array[") && lit.EndsWith(']'))
            || (lit.StartsWith('[') && lit.EndsWith(']'));
    }

    /// <summary>“::” 右侧是否像类型名（排除函数调用等表达式尾巴）：字母数字、括号、方括号、点、逗号与空白。</summary>
    private static bool LooksLikeTypeName(string t) =>
        t.Length > 0 && t.All(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)
            || c is '_' or '[' or ']' or '(' or ')' or ',' or '.');

    private static bool IsArrayCast(string lit, string type)
    {
        if (type.EndsWith("[]") || type.StartsWith('_')) return true;
        var l = lit.ToLowerInvariant();
        return l.StartsWith("array[") || (lit.StartsWith('[') && lit.EndsWith(']'));
    }

    /// <summary>切分 PG 数组大括号（“{”…“}”之间）内的元素：双引号内支持 "" 转义与逗号。</summary>
    private static List<string> SplitPgBraceElems(string inner)
    {
        var result = new List<string>();
        var inQ = false;
        var sb = new StringBuilder();
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (inQ)
            {
                sb.Append(ch);
                if (ch == '"')
                {
                    if (i + 1 < inner.Length && inner[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQ = false;
                }
                continue;
            }
            switch (ch)
            {
                case '"': inQ = true; sb.Append(ch); break;
                case ',': result.Add(sb.ToString().Trim()); sb.Clear(); break;
                default: sb.Append(ch); break;
            }
        }
        if (sb.Length > 0) result.Add(sb.ToString().Trim());
        return result;
    }

    /// <summary>单个数组元素 → JSON 值文本。pgBrace：PG '{}' 格式（元素用双引号）；否则 SQL 表达式格式（元素用单引号）。</summary>
    private static string ArrayElemToJson(string elem, bool pgBrace)
    {
        var e = elem.Trim();
        if (pgBrace)
        {
            if (e.Length >= 2 && e.StartsWith('"') && e.EndsWith('"'))
                return JsonQuote(UnescapePgElem(e[1..^1]));
            var l = e.ToLowerInvariant();
            if (l is "null") return "null";
            if (l is "true" or "t") return "true";
            if (l is "false" or "f") return "false";
            return RegexNumber().IsMatch(e) ? e : JsonQuote(e);
        }
        if (e.Equals("null", StringComparison.OrdinalIgnoreCase)) return "null";
        if (e.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            e.Equals("false", StringComparison.OrdinalIgnoreCase))
            return e.ToLowerInvariant();
        if (e.Length >= 2 && e.StartsWith('\'') && e.EndsWith('\''))
            return JsonQuote(e[1..^1].Replace("''", "'"));
        return RegexNumber().IsMatch(e) ? e : JsonQuote(e);
    }

    private static string UnescapePgElem(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string JsonQuote(string s) => "\"" +
        s.Replace("\\", "\\\\").Replace("\"", "\\\"")
         .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

    private static string SqlQuote(string s) => "'" + s.Replace("'", "''") + "'";

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

    /// <summary>SQL Server 风格顶层 + 拼接（字符串操作数感知，忽略字符串与括号内的 +）。</summary>
    private static bool HasTopLevelPlus(string e)
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
                case '+' when depth == 0: return true;
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
