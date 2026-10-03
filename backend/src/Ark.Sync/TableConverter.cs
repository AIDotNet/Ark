using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;

namespace Ark.Sync;

/// <summary>
/// 源表 Canonical 模型 → 目标方言 Canonical 模型。
/// 类型走 TypeMapper（降级+警告），默认值/生成列走 FunctionTranslator（无法翻译 → error issue）。
/// 翻译后的默认值以 Literal 形式写入（DDL 生成器原样输出），保持“翻译一次”的语义。
/// </summary>
public static class TableConverter
{
    public static (CanonicalTable Converted, IReadOnlyList<ConversionIssue> Issues) Convert(
        CanonicalTable source, ArkDialect from, ArkDialect to)
    {
        var issues = new List<ConversionIssue>();
        var columns = new List<CanonicalColumn>();

        foreach (var col in source.Columns)
        {
            var typeMapping = TypeMapper.FromCanonical(col.Type, to);
            foreach (var w in typeMapping.Warnings)
                issues.Add(new ConversionIssue("warning", $"列 {col.Name}: {w}"));

            var raw = col.DefaultValueSql;
            var kind = col.DefaultKind;
            string? newRaw = raw;
            var newKind = kind;

            if (raw is not null)
            {
                if (kind == ValueExprKind.Unrecognized)
                {
                    issues.Add(new ConversionIssue("warning",
                        $"列 {col.Name}: 默认值表达式 {raw} 无法自动翻译，已原样复制，请人工确认"));
                }
                else if (kind != ValueExprKind.Literal)
                {
                    var emitted = FunctionTranslator.Emit(to, kind, raw);
                    if (emitted is null)
                    {
                        issues.Add(new ConversionIssue("error",
                            $"列 {col.Name}: 默认值 {raw}（{kind}）无法在目标方言 {to} 表达，同步时该默认值将被忽略"));
                    }
                    else
                    {
                        newRaw = emitted;
                        newKind = ValueExprKind.Literal; // 已翻译为目标方言原文，DDL 直接输出
                    }
                }

                // 布尔列的 0/1 字面量默认值：PostgreSQL 要求 true/false（integer 不能隐式转 boolean）
                if (to == ArkDialect.PostgreSQL && col.Type.Id == CanonicalTypeId.Boolean
                    && newKind == ValueExprKind.Literal)
                {
                    newRaw = newRaw switch
                    {
                        "0" => "false",
                        "1" => "true",
                        var r when r.Trim() is "0" or "1" => r.Trim() == "0" ? "false" : "true",
                        var r => r,
                    };
                }
            }

            if (col.IsGenerated)
            {
                if (col.GeneratedExpression is null)
                    issues.Add(new ConversionIssue("error", $"列 {col.Name}: 生成列表达式无法从源库提取，无法同步"));
                else
                    issues.Add(new ConversionIssue("warning",
                        $"列 {col.Name}: 生成列表达式按原文复制（{col.GeneratedExpression}），请确认目标方言兼容"));
            }

            columns.Add(col with
            {
                Type = col.Type,
                DefaultValueSql = newRaw,
                DefaultKind = newKind,
            });
        }

        // 自增的跨库约束
        foreach (var col in columns.Where(c => c.IsAutoIncrement))
        {
            if (to == ArkDialect.SQLite)
            {
                // SQLite INTEGER PRIMARY KEY（rowid 别名）接受任意宽度的单一整数主键
                var singleIntPk = source.PrimaryKeyColumns.Count == 1 &&
                                  string.Equals(source.PrimaryKeyColumns[0], col.Name, StringComparison.OrdinalIgnoreCase) &&
                                  col.Type.Id is CanonicalTypeId.Int16 or CanonicalTypeId.Int32 or CanonicalTypeId.Int64;
                if (!singleIntPk)
                    issues.Add(new ConversionIssue("error",
                        $"列 {col.Name}: SQLite 的自增要求“单一整数主键”，该列不满足，自增语义将丢失"));
            }
            if (to == ArkDialect.MySQL &&
                !source.PrimaryKeyColumns.Contains(col.Name, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ConversionIssue("warning", $"列 {col.Name}: MySQL 的 AUTO_INCREMENT 必须位于主键上，已忽略自增属性"));
            }
        }

        if (source.Columns.Any(c => c.Type.Id == CanonicalTypeId.Unknown))
            issues.Add(new ConversionIssue("warning", "存在未识别的源类型，已按文本降级，请人工核对"));

        var converted = source with { Columns = columns };
        return (converted, issues);
    }
}
