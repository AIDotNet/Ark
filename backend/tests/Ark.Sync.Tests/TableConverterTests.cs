using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Sync;
using Xunit;

namespace Ark.Sync.Tests;

public class GeneratedColumnConvertTests
{
    private static CanonicalColumn Generated(string name, string expression) => new()
    {
        Name = name,
        Type = CanonicalType.Of(CanonicalTypeId.Int32),
        IsGenerated = true,
        GeneratedExpression = expression,
        // 提取端约定：生成列的表达式放 GeneratedExpression，DefaultValueSql 恒为 null
        DefaultValueSql = null,
        DefaultKind = ValueExprKind.None,
    };

    private static CanonicalTable Table(params CanonicalColumn[] cols) => new()
    {
        Name = "t",
        Columns = cols,
        PrimaryKeyColumns = ["id"],
    };

    [Fact]
    public void GeneratedColumn_SameDialect_PassesThroughWithoutWarnings()
    {
        var (converted, issues) = TableConverter.Convert(
            Table(
                new CanonicalColumn { Name = "id", Type = CanonicalType.Long },
                Generated("available_balance", "((cash_balance + gift_balance) - frozen_amount)"),
                new CanonicalColumn { Name = "has_password", Type = CanonicalType.Bool, IsGenerated = true, GeneratedExpression = "password_hash IS NOT NULL" }
            ),
            ArkDialect.PostgreSQL, ArkDialect.PostgreSQL);

        Assert.DoesNotContain(issues, i => i.Severity == "error");
        Assert.Empty(issues);
        var gen = converted.Columns.Single(c => c.Name == "available_balance");
        Assert.Equal("((cash_balance + gift_balance) - frozen_amount)", gen.GeneratedExpression);
        Assert.Null(gen.DefaultValueSql); // 不允许再被当默认值翻译/发射
    }

    [Fact]
    public void GeneratedColumn_CrossDialect_WarnsCopyVerbatim()
    {
        var (_, issues) = TableConverter.Convert(
            Table(Generated("available_balance", "cash_balance + gift_balance")),
            ArkDialect.PostgreSQL, ArkDialect.MySQL);

        Assert.DoesNotContain(issues, i => i.Severity == "error");
        Assert.Contains(issues, i => i.Severity == "warning" && i.Message.Contains("原文复制"));
    }

    [Fact]
    public void GeneratedColumn_MissingExpression_ReportsError()
    {
        var (_, issues) = TableConverter.Convert(
            Table(new CanonicalColumn { Name = "g", Type = CanonicalType.Int, IsGenerated = true }),
            ArkDialect.PostgreSQL, ArkDialect.PostgreSQL);

        Assert.Contains(issues, i => i.Severity == "error" && i.Message.Contains("无法从源库提取"));
    }

    [Fact]
    public void PlainDefault_Unrecognized_SameDialect_CopiesVerbatimWithWarning()
    {
        var col = new CanonicalColumn
        {
            Name = "code",
            Type = CanonicalType.Str,
            DefaultValueSql = "('X' || nextval('seq'))",
            DefaultKind = ValueExprKind.Unrecognized,
        };
        var (converted, issues) = TableConverter.Convert(Table(col), ArkDialect.PostgreSQL, ArkDialect.PostgreSQL);

        Assert.Equal("('X' || nextval('seq'))", converted.Columns.Single(c => c.Name == "code").DefaultValueSql);
        Assert.DoesNotContain(issues, i => i.Severity == "error");
    }
}
