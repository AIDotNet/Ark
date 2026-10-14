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
    [InlineData(ArkDialect.PostgreSQL, "true", ValueExprKind.BooleanLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "false", ValueExprKind.BooleanLiteral)]
    [InlineData(ArkDialect.MySQL, "TRUE", ValueExprKind.BooleanLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "'{}'::smallint[]", ValueExprKind.ArrayLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "'{}'::text[]", ValueExprKind.ArrayLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "ARRAY['a','b']::text[]", ValueExprKind.ArrayLiteral)]
    [InlineData(ArkDialect.DuckDB, "['a', 'b']", ValueExprKind.ArrayLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "'active'::order_status", ValueExprKind.TypedLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "'42'::int", ValueExprKind.TypedLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "'a::b'::text", ValueExprKind.TypedLiteral)]
    [InlineData(ArkDialect.PostgreSQL, "now()::timestamptz", ValueExprKind.Unrecognized)]
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

    // ---------- 布尔 / 数组 / 类型转换字面量 ----------

    [Theory]
    [InlineData(ArkDialect.SqlServer, "true", "1")]
    [InlineData(ArkDialect.SqlServer, "false", "0")]
    [InlineData(ArkDialect.DM8, "false", "0")]
    [InlineData(ArkDialect.SQLite, "true", "1")]
    [InlineData(ArkDialect.PostgreSQL, "true", "true")]
    [InlineData(ArkDialect.MySQL, "true", "true")]
    [InlineData(ArkDialect.KingbaseES, "false", "false")]
    public void Emit_BoolLiteral(ArkDialect target, string raw, string expected)
    {
        Assert.Equal(expected, FunctionTranslator.Emit(target, ValueExprKind.BooleanLiteral, raw));
    }

    [Theory]
    [InlineData(ArkDialect.PostgreSQL, "'{}'::text[]", "'[]'")]
    [InlineData(ArkDialect.MySQL, "'{}'::smallint[]", "('[]')")]
    [InlineData(ArkDialect.SqlServer, "'{\"a\",\"b\"}'::text[]", "'[\"a\",\"b\"]'")]
    [InlineData(ArkDialect.PostgreSQL, "'{1,2}'::smallint[]", "'[1,2]'")]
    [InlineData(ArkDialect.ClickHouse, "'{\"a,b\",\"c\"}'::text[]", "'[\"a,b\",\"c\"]'")]
    [InlineData(ArkDialect.DuckDB, "ARRAY['a','b']::text[]", "'[\"a\",\"b\"]'")]
    [InlineData(ArkDialect.PostgreSQL, "ARRAY[1,2]::int[]", "'[1,2]'")]
    public void Emit_ArrayLiteral_BecomesJson(ArkDialect target, string raw, string expected)
    {
        Assert.Equal(expected, FunctionTranslator.Emit(target, ValueExprKind.ArrayLiteral, raw));
    }

    [Fact]
    public void Emit_ArrayLiteral_ToDm8Clob_IsQuotedJson()
    {
        Assert.Equal("'[]'", FunctionTranslator.Emit(ArkDialect.DM8, ValueExprKind.ArrayLiteral, "'{}'::text[]"));
    }

    [Theory]
    [InlineData(ArkDialect.MySQL, "'active'::order_status", "'active'")]
    [InlineData(ArkDialect.PostgreSQL, "'{}'::jsonb", "'{}'")]
    [InlineData(ArkDialect.SqlServer, "'42'::int", "'42'")]
    public void Emit_TypedLiteral_StripsCast(ArkDialect target, string raw, string expected)
    {
        Assert.Equal(expected, FunctionTranslator.Emit(target, ValueExprKind.TypedLiteral, raw));
    }
}
