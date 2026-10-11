using Ark.Core.Metadata;
using Ark.Core.Typing;
using Xunit;

namespace Ark.Core.Tests;

/// <summary>DuckDB / ClickHouse 的类型双向映射与函数翻译断言（分支已内置于 TypeMapper/FunctionTranslator）。</summary>
public class DuckDbClickHouseMappingTests
{
    // ============================== DuckDB ==============================

    // ---------- provider 类型 → 规范类型 ----------

    [Theory]
    [InlineData("INTEGER", CanonicalTypeId.Int32)]
    [InlineData("BIGINT", CanonicalTypeId.Int64)]
    [InlineData("SMALLINT", CanonicalTypeId.Int16)]
    [InlineData("VARCHAR", CanonicalTypeId.VarChar)]
    [InlineData("TEXT", CanonicalTypeId.Text)]
    [InlineData("BOOLEAN", CanonicalTypeId.Boolean)]
    [InlineData("DOUBLE", CanonicalTypeId.Double)]
    [InlineData("BLOB", CanonicalTypeId.Binary)]
    [InlineData("UUID", CanonicalTypeId.Guid)]
    [InlineData("DATE", CanonicalTypeId.Date)]
    [InlineData("TIMESTAMP", CanonicalTypeId.DateTime)]
    [InlineData("TIMESTAMPTZ", CanonicalTypeId.DateTimeOffset)]
    [InlineData("TIMESTAMP WITH TIME ZONE", CanonicalTypeId.DateTimeOffset)] // duckdb_columns() 返回形式
    [InlineData("STRUCT(a INTEGER, b VARCHAR)", CanonicalTypeId.Json)]
    [InlineData("MAP(INTEGER, INTEGER)", CanonicalTypeId.Json)]
    public void DuckDbTypes_MapToCanonical(string input, CanonicalTypeId expected)
    {
        var t = TypeMapper.ToCanonical(ArkDialect.DuckDB, input, null, null, null);
        Assert.Equal(expected, t.Id);
    }

    [Fact]
    public void DuckDbBracketedList_NotMapped_GivenMapperDealsUnknown()
    {
        // duckdb_columns() 的方括号写法（VARCHAR[]）不在映射表（list/struct/map 写法才映射 Json）→ Unknown，
        // 由 Provider 读取时按 data_type 兜底 CAST，DDL 生成时降级 VARCHAR 并警告
        var t = TypeMapper.ToCanonical(ArkDialect.DuckDB, "VARCHAR[]", null, null, null);
        Assert.Equal(CanonicalTypeId.Unknown, t.Id);
    }

    [Fact]
    public void DuckDbDecimal_ParsesPrecisionScale()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.DuckDB, "DECIMAL(10,2)", 10, 10, 2);
        Assert.Equal(CanonicalTypeId.Decimal, t.Id);
        Assert.Equal(10, t.Precision);
        Assert.Equal(2, t.Scale);
    }

    [Fact]
    public void DuckDbHugeInt_MapsToDecimal38()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.DuckDB, "HUGEINT", null, null, null);
        Assert.Equal(CanonicalTypeId.Decimal, t.Id);
        Assert.Equal(38, t.Precision);
        Assert.Equal(0, t.Scale);
    }

    // ---------- 规范类型 → DuckDB ----------

    [Fact]
    public void Canonical_ToDuckDb_CoreTypes()
    {
        Assert.Equal("TIMESTAMPTZ", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTimeOffset), ArkDialect.DuckDB).Sql);
        Assert.Equal("UUID", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Guid), ArkDialect.DuckDB).Sql);
        Assert.Equal("JSON", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Json), ArkDialect.DuckDB).Sql);
        Assert.Equal("BOOLEAN", TypeMapper.FromCanonical(CanonicalType.Bool, ArkDialect.DuckDB).Sql);
        Assert.Equal("BLOB", TypeMapper.FromCanonical(CanonicalType.Blob, ArkDialect.DuckDB).Sql);
    }

    [Fact]
    public void Canonical_Decimal_ToDuckDb_EmitsScale()
    {
        var m = TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.Decimal, Precision: 10, Scale: 2), ArkDialect.DuckDB);
        Assert.Equal("decimal(10,2)", m.Sql);
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void Canonical_Unknown_ToDuckDb_WarnsFallback()
    {
        var m = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Unknown), ArkDialect.DuckDB);
        Assert.Equal("VARCHAR", m.Sql);
        Assert.NotEmpty(m.Warnings);
    }

    // ---------- 函数翻译（DuckDB） ----------

    [Theory]
    [InlineData("uuid()", ValueExprKind.UuidV4)]
    [InlineData("gen_random_uuid()", ValueExprKind.UuidV4)]
    [InlineData("now()", ValueExprKind.CurrentTimestamp)]
    [InlineData("current_timestamp", ValueExprKind.CurrentTimestamp)]
    [InlineData("coalesce(a, b)", ValueExprKind.Coalesce)]
    [InlineData("concat(a, b)", ValueExprKind.Concat)]
    [InlineData("'x' || y", ValueExprKind.Concat)]
    [InlineData("'literal'", ValueExprKind.Literal)]
    public void DuckDbExpressions_AreRecognized(string expr, ValueExprKind expected)
    {
        Assert.Equal(expected, FunctionTranslator.Recognize(ArkDialect.DuckDB, expr));
    }

    [Fact]
    public void DuckDbEmit_UuidAndTimestamp()
    {
        Assert.Equal("uuid()", FunctionTranslator.Emit(ArkDialect.DuckDB, ValueExprKind.UuidV4, "gen_random_uuid()"));
        Assert.Equal("CURRENT_TIMESTAMP", FunctionTranslator.Emit(ArkDialect.DuckDB, ValueExprKind.CurrentTimestamp, "now()"));
        Assert.Equal("CURRENT_DATE", FunctionTranslator.Emit(ArkDialect.DuckDB, ValueExprKind.CurrentDate, "current_date"));
    }

    [Fact]
    public void DuckDbEmit_Concat_UsesConcatFunction()
    {
        var emitted = FunctionTranslator.Emit(ArkDialect.DuckDB, ValueExprKind.Concat, "'a' || b");
        Assert.Equal("CONCAT('a', b)", emitted);
    }

    // ============================== ClickHouse ==============================

    // ---------- provider 类型 → 规范类型 ----------

    [Theory]
    [InlineData("String", CanonicalTypeId.Text)]
    [InlineData("Int32", CanonicalTypeId.Int32)]
    [InlineData("Int64", CanonicalTypeId.Int64)]
    [InlineData("UInt8", CanonicalTypeId.Int16)]
    [InlineData("UInt32", CanonicalTypeId.Int64)] // 无符号向上有符号加宽
    [InlineData("Float64", CanonicalTypeId.Double)]
    [InlineData("UUID", CanonicalTypeId.Guid)]
    [InlineData("Date", CanonicalTypeId.Date)]
    [InlineData("DateTime", CanonicalTypeId.DateTime)]
    [InlineData("Array(Int32)", CanonicalTypeId.Json)]
    [InlineData("Map(String, Int64)", CanonicalTypeId.Json)]
    [InlineData("Tuple(a Int32)", CanonicalTypeId.Json)]
    public void ClickHouseTypes_MapToCanonical(string input, CanonicalTypeId expected)
    {
        var t = TypeMapper.ToCanonical(ArkDialect.ClickHouse, input, null, null, null);
        Assert.Equal(expected, t.Id);
    }

    [Fact]
    public void ClickHouse_UInt64_WidensToDecimal20_0()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.ClickHouse, "UInt64", null, null, null);
        Assert.Equal(CanonicalTypeId.Decimal, t.Id);
        Assert.Equal(20, t.Precision);
        Assert.Equal(0, t.Scale);
    }

    [Fact]
    public void ClickHouse_Decimal_And_FixedString_ParseArgs()
    {
        var d = TypeMapper.ToCanonical(ArkDialect.ClickHouse, "Decimal(18,4)", null, null, null);
        Assert.Equal(CanonicalTypeId.Decimal, d.Id);
        Assert.Equal(18, d.Precision);
        Assert.Equal(4, d.Scale);

        var f = TypeMapper.ToCanonical(ArkDialect.ClickHouse, "FixedString(10)", null, null, null);
        Assert.Equal(CanonicalTypeId.Char, f.Id);
        Assert.Equal(10, f.Length);
    }

    [Fact]
    public void ClickHouse_DateTime64_MapsToDateTime()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.ClickHouse, "DateTime64(3)", null, null, null);
        Assert.Equal(CanonicalTypeId.DateTime, t.Id);
    }

    // Nullable(String) 由 Provider 解包后传 "String"：验证解包输入走 Text
    [Fact]
    public void ClickHouse_UnwrappedNullableString_MapsToText()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.ClickHouse, "String", null, null, null);
        Assert.Equal(CanonicalTypeId.Text, t.Id);
    }

    // ---------- 规范类型 → ClickHouse ----------

    [Fact]
    public void Canonical_ToClickHouse_CoreTypes()
    {
        Assert.Equal("String", TypeMapper.FromCanonical(CanonicalType.Str, ArkDialect.ClickHouse).Sql);
        Assert.Equal("UUID", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Guid), ArkDialect.ClickHouse).Sql);
        Assert.Equal("Int64", TypeMapper.FromCanonical(CanonicalType.Long, ArkDialect.ClickHouse).Sql);
        Assert.Equal("Float64", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Double), ArkDialect.ClickHouse).Sql);
        Assert.Equal("Date", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Date), ArkDialect.ClickHouse).Sql);
    }

    [Fact]
    public void Canonical_Bool_ToClickHouse_IsUInt8_WithWarning()
    {
        var m = TypeMapper.FromCanonical(CanonicalType.Bool, ArkDialect.ClickHouse);
        Assert.Equal("UInt8", m.Sql);
        Assert.NotEmpty(m.Warnings);
    }

    [Fact]
    public void Canonical_DateTimeOffset_ToClickHouse_WarnsTimezone()
    {
        var m = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTimeOffset), ArkDialect.ClickHouse);
        Assert.Equal("DateTime", m.Sql);
        Assert.Contains(m.Warnings, w => w.Contains("时区"));
    }

    [Fact]
    public void Canonical_Decimal_ToClickHouse_EmitsPrecision()
    {
        var m = TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.Decimal, Precision: 20, Scale: 0), ArkDialect.ClickHouse);
        Assert.Equal("decimal(20,0)", m.Sql);
    }

    [Fact]
    public void Canonical_Json_And_Time_ToClickHouse_DegradeToString()
    {
        var j = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Json), ArkDialect.ClickHouse);
        Assert.Equal("String", j.Sql);
        Assert.NotEmpty(j.Warnings);
        var t = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Time), ArkDialect.ClickHouse);
        Assert.Equal("String", t.Sql);
        Assert.NotEmpty(t.Warnings);
    }

    // ---------- 函数翻译（ClickHouse） ----------

    [Theory]
    [InlineData("today()", ValueExprKind.CurrentDate)]
    [InlineData("now()", ValueExprKind.CurrentTimestamp)]
    [InlineData("now64(3)", ValueExprKind.CurrentTimestamp)]
    [InlineData("generateUUIDv4()", ValueExprKind.UuidV4)]
    [InlineData("uuid()", ValueExprKind.UuidV4)]
    [InlineData("coalesce(a, b)", ValueExprKind.Coalesce)]
    [InlineData("concat(a, b)", ValueExprKind.Concat)]
    [InlineData("concatWithSeparator('-', a, b)", ValueExprKind.Concat)]
    [InlineData("'literal'", ValueExprKind.Literal)]
    public void ClickHouseExpressions_AreRecognized(string expr, ValueExprKind expected)
    {
        Assert.Equal(expected, FunctionTranslator.Recognize(ArkDialect.ClickHouse, expr));
    }

    [Fact]
    public void ClickHouseEmit_UuidAndToday()
    {
        Assert.Equal("generateUUIDv4()", FunctionTranslator.Emit(ArkDialect.ClickHouse, ValueExprKind.UuidV4, "uuid()"));
        Assert.Equal("CURRENT_DATE", FunctionTranslator.Emit(ArkDialect.ClickHouse, ValueExprKind.CurrentDate, "today()"));
        Assert.Equal("now()", FunctionTranslator.Emit(ArkDialect.ClickHouse, ValueExprKind.CurrentTimestamp, "now64(3)"));
    }

    [Fact]
    public void ClickHouseEmit_Concat_PreservesConcatFunction()
    {
        var emitted = FunctionTranslator.Emit(ArkDialect.ClickHouse, ValueExprKind.Concat, "a || b");
        Assert.Equal("CONCAT(a, b)", emitted);
    }

    // ---------- 跨方言一致性（同一语义 → 同一规范类型） ----------

    [Fact]
    public void Uuid_DefaultValue_RoundTrips_FromClickHouse_ToDuckDb()
    {
        var kind = FunctionTranslator.Recognize(ArkDialect.ClickHouse, "generateUUIDv4()");
        var emitted = FunctionTranslator.Emit(ArkDialect.DuckDB, kind, "generateUUIDv4()");
        Assert.Equal("uuid()", emitted);
    }

    [Fact]
    public void CurrentTimestamp_RoundTrips_FromDuckDb_ToClickHouse()
    {
        var kind = FunctionTranslator.Recognize(ArkDialect.DuckDB, "now()");
        var emitted = FunctionTranslator.Emit(ArkDialect.ClickHouse, kind, "now()");
        Assert.Equal("now()", emitted);
    }
}
