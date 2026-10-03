using Ark.Core.Metadata;
using Ark.Core.Typing;
using Xunit;

namespace Ark.Core.Tests;

public class FunctionTranslatorTests
{
    // ---------- 识别 ----------

    [Theory]
    [InlineData(ArkDialect.PostgreSQL, "now()", ValueExprKind.CurrentTimestamp)]
    [InlineData(ArkDialect.PostgreSQL, "CURRENT_TIMESTAMP", ValueExprKind.CurrentTimestamp)]
    [InlineData(ArkDialect.MySQL, "CURRENT_TIMESTAMP(6)", ValueExprKind.CurrentTimestamp)]
    [InlineData(ArkDialect.MySQL, "uuid()", ValueExprKind.UuidV4)]
    [InlineData(ArkDialect.PostgreSQL, "gen_random_uuid()", ValueExprKind.UuidV4)]
    [InlineData(ArkDialect.PostgreSQL, "uuid_generate_v4()", ValueExprKind.UuidV4)]
    [InlineData(ArkDialect.SQLite, "datetime('now')", ValueExprKind.CurrentTimestamp)]
    [InlineData(ArkDialect.SQLite, "date('now')", ValueExprKind.CurrentDate)]
    [InlineData(ArkDialect.MySQL, "CURDATE()", ValueExprKind.CurrentDate)]
    [InlineData(ArkDialect.MySQL, "IFNULL(a, b)", ValueExprKind.Coalesce)]
    [InlineData(ArkDialect.PostgreSQL, "COALESCE(x, 'n/a')", ValueExprKind.Coalesce)]
    [InlineData(ArkDialect.MySQL, "CONCAT(a, b)", ValueExprKind.Concat)]
    [InlineData(ArkDialect.PostgreSQL, "a || b", ValueExprKind.Concat)]
    [InlineData(ArkDialect.MySQL, "'fixed'", ValueExprKind.Literal)]
    [InlineData(ArkDialect.MySQL, "42", ValueExprKind.Literal)]
    [InlineData(ArkDialect.MySQL, "custom_biz_func(1)", ValueExprKind.Unrecognized)]
    public void Recognize_Matrix(ArkDialect dialect, string expr, ValueExprKind expected)
    {
        Assert.Equal(expected, FunctionTranslator.Recognize(dialect, expr));
    }

    // ---------- 发射 ----------

    [Fact]
    public void Uuid_ToPg_IsGenRandomUuid()
    {
        Assert.Equal("gen_random_uuid()", FunctionTranslator.Emit(ArkDialect.PostgreSQL, ValueExprKind.UuidV4, "uuid()"));
    }

    [Fact]
    public void Uuid_ToMySql_IsUuid()
    {
        Assert.Equal("UUID()", FunctionTranslator.Emit(ArkDialect.MySQL, ValueExprKind.UuidV4, "gen_random_uuid()"));
    }

    [Fact]
    public void Uuid_ToSqlite_IsUnsupported()
    {
        Assert.Null(FunctionTranslator.Emit(ArkDialect.SQLite, ValueExprKind.UuidV4, "uuid()"));
    }

    [Fact]
    public void Now_TranslatesAcrossAllThree()
    {
        Assert.NotNull(FunctionTranslator.Emit(ArkDialect.PostgreSQL, ValueExprKind.CurrentTimestamp, "datetime('now')"));
        Assert.NotNull(FunctionTranslator.Emit(ArkDialect.MySQL, ValueExprKind.CurrentTimestamp, "datetime('now')"));
        Assert.NotNull(FunctionTranslator.Emit(ArkDialect.SQLite, ValueExprKind.CurrentTimestamp, "now()"));
    }

    [Fact]
    public void Ifnull_ToPg_BecomesCoalesce()
    {
        var emitted = FunctionTranslator.Emit(ArkDialect.PostgreSQL, ValueExprKind.Coalesce, "IFNULL(a, b)");
        Assert.Equal("COALESCE(a, b)", emitted);
    }

    [Fact]
    public void Concat_ToSqlite_UsesConcatOperator()
    {
        var emitted = FunctionTranslator.Emit(ArkDialect.SQLite, ValueExprKind.Concat, "CONCAT(a, b)");
        Assert.Equal("a || b", emitted);
    }

    [Fact]
    public void Concat_ToMySql_KeepsConcatFn()
    {
        var emitted = FunctionTranslator.Emit(ArkDialect.MySQL, ValueExprKind.Concat, "a || b");
        Assert.Equal("CONCAT(a, b)", emitted);
    }

    [Fact]
    public void SplitTopLevelArgs_HandlesNestedAndStrings()
    {
        var args = FunctionTranslator.SplitTopLevelArgs("a, F(x, y), 'a,b', (p || q)");
        Assert.Equal(["a", "F(x, y)", "'a,b'", "(p || q)"], args);
    }
}
