using System.Globalization;
using System.Text;
using Ark.Core.Metadata;
using Ark.Core.Sync;

namespace Ark.Sync;

/// <summary>脱敏规则应用器：仅在写目标路径生效；Diff 哈希用源值计算保证对比正确。</summary>
public static class Masking
{
    /// <summary>编译规则：返回 列名(目标列序) → 值变换 的委托表。</summary>
    public static Dictionary<int, Func<Random, object?, object?>> Compile(
        IReadOnlyList<MaskRule>? rules, IReadOnlyList<string> targetColNames,
        IReadOnlyList<CanonicalColumn>? targetCols = null)
    {
        var result = new Dictionary<int, Func<Random, object?, object?>>();
        if (rules is null || rules.Count == 0) return result;
        var unique = targetCols?
            .Where(c => c.IsPrimaryKey || targetCols!.Count(x => x.Name == c.Name) == 1 && c.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase) && c.Type.Id is CanonicalTypeId.Int32 or CanonicalTypeId.Int64)
            .Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
        _ = unique; // 保留：唯一性警告在计划期完成，此处不阻塞

        for (var i = 0; i < targetColNames.Count; i++)
        {
            var rule = Match(rules, targetColNames[i]);
            if (rule is null) continue;
            result[i] = rule.Kind switch
            {
                MaskRuleKind.Null => (_, _) => null,
                MaskRuleKind.Fixed => (_, _) => rule.Value,
                MaskRuleKind.RandomInt => (rnd, _) =>
                    int.TryParse(rule.Value, CultureInfo.InvariantCulture, out var max) && max > 0
                        ? (object)rnd.Next(0, max)
                        : rnd.Next(),
                MaskRuleKind.RandomLetter => (rnd, _) => RandomLetters(rnd, Math.Max(1, int.TryParse(rule.Value, out var n) ? n : 8)),
                MaskRuleKind.RandomDate => (rnd, _) => DateTime.UtcNow.AddDays(-rnd.Next(0, 3650)),
                MaskRuleKind.SqlExpr => (_, orig) => orig, // SqlExpr 由方言写路径原样拼接（见 SqlExprColumns）
                _ => (_, v) => v,
            };
        }
        return result;
    }

    /// <summary>SqlExpr 规则的目标列名集合（写入时以表达式替代参数占位）。</summary>
    public static HashSet<string> SqlExprColumns(IReadOnlyList<MaskRule>? rules, IReadOnlyList<string> targetColNames)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rules is null) return set;
        foreach (var col in targetColNames)
        {
            var rule = Match(rules, col);
            if (rule is { Kind: MaskRuleKind.SqlExpr } && !string.IsNullOrWhiteSpace(rule.Value))
                set.Add(col);
        }
        return set;
    }

    public static MaskRule? Match(IReadOnlyList<MaskRule> rules, string column)
    {
        foreach (var r in rules)
        {
            if (WildcardMatch(r.ColumnPattern, column)) return r;
        }
        return null;
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        if (pattern == "*") return true;
        var idx = pattern.IndexOf('*', StringComparison.Ordinal);
        if (idx < 0) return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        var prefix = pattern[..idx];
        var suffix = pattern[(idx + 1)..];
        return value.Length >= prefix.Length + suffix.Length
               && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static string RandomLetters(Random rnd, int n)
    {
        var sb = new StringBuilder(n);
        for (var i = 0; i < n; i++) sb.Append((char)('a' + rnd.Next(26)));
        return sb.ToString();
    }
}
