using System.Text.Json;
using Ark.Core.Metadata;
using Ark.Core.Typing;
using Xunit;

namespace Ark.Core.Tests;

public class PgArrayTextTests
{
    // ---------- IsArrayType ----------

    [Theory]
    [InlineData("smallint[]", true)]
    [InlineData("integer[]", true)]
    [InlineData("character varying(255)[]", true)]
    [InlineData("_text", true)] // format_type 的内部名形态
    [InlineData("integer", false)]
    [InlineData("jsonb", false)]
    [InlineData("numeric(10,2)", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsArrayType_DetectsPgArrayForms(string? native, bool expected)
        => Assert.Equal(expected, PgArrayText.IsArrayType(native));

    // ---------- FromObject（导出：CSV / SQL dump / COPY）----------

    [Fact]
    public void FromObject_RendersClrArray_AsPgLiteral()
        => Assert.Equal("{1,2,3}", PgArrayText.FromObject(new short[] { 1, 2, 3 }));

    [Fact]
    public void FromObject_BoolArray_UsesLowercaseLiterals()
        => Assert.Equal("{true,false}", PgArrayText.FromObject(new bool[] { true, false }));

    [Fact]
    public void FromObject_QuotesSpecialElements_AndNull()
        => Assert.Equal(
            """{"a,b","c\"d","","{\"x\":1}"," hello world ",NULL}""",
            PgArrayText.FromObject(new object?[] { "a,b", "c\"d", "", "{\"x\":1}", " hello world ", null }));

    [Fact]
    public void FromObject_NestedArray_KeepsNestedBraces()
        => Assert.Equal("{{1,2},3}", PgArrayText.FromObject(new object[] { new[] { 1, 2 }, 3 }));

    [Fact]
    public void FromObject_EmptyArray_IsEmptyBraces()
        => Assert.Equal("{}", PgArrayText.FromObject(Array.Empty<int>()));

    [Fact]
    public void FromObject_Scalar_PassesThrough()
        => Assert.Equal("42", PgArrayText.FromObject(42));

    // ---------- FromJsonElement（导入取值）----------

    [Fact]
    public void FromJsonElement_JsonArray_ConvertsToPgLiteral()
    {
        using var doc = JsonDocument.Parse("""[1, 2, "a,b"]""");
        Assert.Equal("""{1,2,"a,b"}""", PgArrayText.FromJsonElement(doc.RootElement));
    }

    [Fact]
    public void FromJsonElement_JsonArrayWithNull_ConvertsToNullElement()
    {
        using var doc = JsonDocument.Parse("""[1, null]""");
        Assert.Equal("{1,NULL}", PgArrayText.FromJsonElement(doc.RootElement));
    }

    [Fact]
    public void FromJsonElement_PgLiteralString_PassesThrough() // CSV 往返：{1,2,3} 原样
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize("{1,2,3}"));
        Assert.Equal("{1,2,3}", PgArrayText.FromJsonElement(doc.RootElement));
    }

    [Fact]
    public void FromJsonElement_JsonArrayText_InString_Converts() // 手写 CSV 里的 JSON 风格数组；无特殊字符的元素不加引号
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize("[1,\"a\"]"));
        Assert.Equal("{1,a}", PgArrayText.FromJsonElement(doc.RootElement));
    }

    [Fact]
    public void FromJsonElement_PlainString_PassesThrough()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize("hello"));
        Assert.Equal("hello", PgArrayText.FromJsonElement(doc.RootElement));
    }

    [Fact]
    public void FromJsonElement_Number_EmitsRawText()
    {
        using var doc = JsonDocument.Parse("7");
        Assert.Equal("7", PgArrayText.FromJsonElement(doc.RootElement));
    }

    // ---------- PgCastFor（42804 修复）----------

    [Fact]
    public void PgCastFor_PrefersNativeType_ForArrayColumn()
    {
        var col = new CanonicalColumn
        {
            Name = "capabilities",
            Type = CanonicalType.Of(CanonicalTypeId.Json), // smallint[] 在规范模型中降级为 Json
            NativeType = "smallint[]",
        };
        Assert.Equal("smallint[]", TypeMapper.PgCastFor(col));
    }

    [Fact]
    public void PgCastFor_PrefersNativeType_WithModifiers()
    {
        var col = new CanonicalColumn
        {
            Name = "amount",
            Type = new CanonicalType(CanonicalTypeId.Decimal, Precision: 10, Scale: 2),
            NativeType = "numeric(10,2)",
        };
        Assert.Equal("numeric(10,2)", TypeMapper.PgCastFor(col));
    }

    [Fact]
    public void PgCastFor_FallsBackToCanonical_WithoutNative()
    {
        var col = new CanonicalColumn { Name = "payload", Type = CanonicalType.Of(CanonicalTypeId.Json) };
        Assert.Equal("jsonb", TypeMapper.PgCastFor(col));
    }

    [Fact]
    public void PgCastFor_GeneratedColumn_ReturnsNull()
    {
        var col = new CanonicalColumn
        {
            Name = "g",
            Type = CanonicalType.Of(CanonicalTypeId.Json),
            NativeType = "smallint[]",
            IsGenerated = true,
        };
        Assert.Null(TypeMapper.PgCastFor(col));
    }
}
