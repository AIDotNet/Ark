using Ark.Core.Metadata;
using Ark.Core.Typing;
using Xunit;

namespace Ark.Core.Tests;

/// <summary>MariaDB / SQL Server 的类型映射与函数翻译用例（双方言 ≥12 断言）。</summary>
public class MariaDbSqlServerMappingTests
{
    // ============================ MariaDB ============================

    [Theory]
    [InlineData("tinyint(1)", CanonicalTypeId.Boolean)]
    [InlineData("bigint", CanonicalTypeId.Int64)]
    [InlineData("datetime", CanonicalTypeId.DateTime)]
    [InlineData("json", CanonicalTypeId.Json)]
    [InlineData("double", CanonicalTypeId.Double)]
    [InlineData("blob", CanonicalTypeId.Binary)]
    [InlineData("longtext", CanonicalTypeId.Text)] // JSON 列在 MariaDB 的实际物理类型
    public void MariaDbTypes_MapToCanonical(string input, CanonicalTypeId expected)
    {
        var t = TypeMapper.ToCanonical(ArkDialect.MariaDB, input, null, null, null);
        Assert.Equal(expected, t.Id);
    }

    [Fact]
    public void MariaDbVarChar_ParsesLength()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.MariaDB, "varchar(120)", null, null, null);
        Assert.Equal(CanonicalTypeId.VarChar, t.Id);
        Assert.Equal(120, t.Length);
    }

    [Fact]
    public void MariaDbDecimal_ParsesPrecisionScale()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.MariaDB, "decimal", 12, 12, 2);
        Assert.Equal(CanonicalTypeId.Decimal, t.Id);
        Assert.Equal(12, t.Precision);
        Assert.Equal(2, t.Scale);
    }

    // MariaDB 10.7+ 原生 uuid 类型：共享层已补齐 MyToCanonical 的 uuid 分支 → Guid。
    [Fact]
    public void MariaDbUuidType_MapsToGuid()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.MariaDB, "uuid", null, null, null);
        Assert.Equal(CanonicalTypeId.Guid, t.Id);
    }

    [Fact]
    public void Canonical_ToMariaDb_GuidIsChar36_BooleanWarnsTinyint()
    {
        var g = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Guid), ArkDialect.MariaDB);
        Assert.Equal("char(36)", g.Sql);
        Assert.Empty(g.Warnings);

        var b = TypeMapper.FromCanonical(CanonicalType.Bool, ArkDialect.MariaDB);
        Assert.Equal("tinyint(1)", b.Sql);
        Assert.NotEmpty(b.Warnings);
    }

    [Fact]
    public void Canonical_ToMariaDb_DateTimeOffsetLosesTz()
    {
        var m = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTimeOffset), ArkDialect.MariaDB);
        Assert.Equal("datetime", m.Sql);
        Assert.Contains(m.Warnings, w => w.Contains("时区"));
    }

    [Fact]
    public void Canonical_ToMariaDb_LongVarChar_DegradesToText()
    {
        var m = TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.VarChar, Length: 100000), ArkDialect.MariaDB);
        Assert.Equal("text", m.Sql);
        Assert.NotEmpty(m.Warnings);
    }

    [Fact]
    public void MariaDb_FunctionTranslator_RoundTrips()
    {
        Assert.Equal(ValueExprKind.UuidV4, FunctionTranslator.Recognize(ArkDialect.MariaDB, "uuid()"));
        Assert.Equal("UUID()", FunctionTranslator.Emit(ArkDialect.MariaDB, ValueExprKind.UuidV4, "uuid()"));

        Assert.Equal(ValueExprKind.CurrentTimestamp, FunctionTranslator.Recognize(ArkDialect.MariaDB, "now()"));
        Assert.Equal("CURRENT_TIMESTAMP",
            FunctionTranslator.Emit(ArkDialect.MariaDB, ValueExprKind.CurrentTimestamp, "now()"));

        Assert.Equal(ValueExprKind.Coalesce, FunctionTranslator.Recognize(ArkDialect.MariaDB, "ifnull(a, b)"));
        Assert.Equal("COALESCE(a, b)",
            FunctionTranslator.Emit(ArkDialect.MariaDB, ValueExprKind.Coalesce, "ifnull(a, b)"));

        Assert.Equal(ValueExprKind.CurrentDate, FunctionTranslator.Recognize(ArkDialect.MariaDB, "curdate()"));
        Assert.Equal("CURRENT_DATE",
            FunctionTranslator.Emit(ArkDialect.MariaDB, ValueExprKind.CurrentDate, "curdate()"));

        // PG 风格 now() 默认值翻译为 MariaDB
        Assert.Equal("CURRENT_TIMESTAMP",
            FunctionTranslator.Emit(ArkDialect.MariaDB, ValueExprKind.CurrentTimestamp, "now()"));
    }

    [Fact]
    public void MariaDb_Dialects_Get_ExposesMySqlLikeTraits()
    {
        var d = Dialects.Get(ArkDialect.MariaDB);
        Assert.Equal(" IGNORE", d.SkipExistingClause);
        Assert.Equal(65000, d.MaxBatchParams);
        Assert.Equal('`', d.QuoteOpen);
        Assert.True(d.BackslashEscapes);
        Assert.False(d.SupportsSchemas);
        Assert.Contains(ArkDialect.MariaDB, DialectDescriptor.MySqlLikeDialects);
        Assert.Equal("`a`.`b`", d.QuoteQualified("a", null, "b"));
    }

    // ============================ SQL Server ============================

    [Theory]
    [InlineData("bit", CanonicalTypeId.Boolean)]
    [InlineData("uniqueidentifier", CanonicalTypeId.Guid)]
    [InlineData("datetimeoffset", CanonicalTypeId.DateTimeOffset)]
    [InlineData("datetime2", CanonicalTypeId.DateTime)]
    [InlineData("int", CanonicalTypeId.Int32)]
    [InlineData("float", CanonicalTypeId.Double)]
    [InlineData("xml", CanonicalTypeId.Xml)]
    [InlineData("nvarchar(max)", CanonicalTypeId.Text)]
    [InlineData("varbinary(max)", CanonicalTypeId.Binary)]
    public void SqlServerTypes_MapToCanonical(string input, CanonicalTypeId expected)
    {
        var t = TypeMapper.ToCanonical(ArkDialect.SqlServer, input, null, null, null);
        Assert.Equal(expected, t.Id);
    }

    [Fact]
    public void SqlServerNVarChar_ParsesLength()
    {
        // nvarchar(50)：从字符串形式解析
        var fromName = TypeMapper.ToCanonical(ArkDialect.SqlServer, "nvarchar(50)", null, null, null);
        Assert.Equal(CanonicalTypeId.VarChar, fromName.Id);
        Assert.Equal(50, fromName.Length);

        // Provider 读 sys.columns 时传 max_length/2 的字符数
        var fromLength = TypeMapper.ToCanonical(ArkDialect.SqlServer, "nvarchar", 50, null, null);
        Assert.Equal(50, fromLength.Length);
    }

    [Fact]
    public void SqlServerDecimal_ParsesPrecisionScale()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.SqlServer, "decimal", null, 18, 2);
        Assert.Equal(CanonicalTypeId.Decimal, t.Id);
        Assert.Equal(18, t.Precision);
        Assert.Equal(2, t.Scale);
    }

    [Fact]
    public void Canonical_ToSqlServer_PrimitiveMappings()
    {
        Assert.Equal("bit", TypeMapper.FromCanonical(CanonicalType.Bool, ArkDialect.SqlServer).Sql);
        Assert.Equal("uniqueidentifier", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Guid), ArkDialect.SqlServer).Sql);
        Assert.Equal("datetimeoffset", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTimeOffset), ArkDialect.SqlServer).Sql);
        Assert.Equal("nvarchar(max)", TypeMapper.FromCanonical(CanonicalType.Str, ArkDialect.SqlServer).Sql);
        Assert.Equal("datetime2", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTime), ArkDialect.SqlServer).Sql);
    }

    [Fact]
    public void Canonical_ToSqlServer_VarChar_WarnsAndDegrades()
    {
        var noLen = TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.VarChar), ArkDialect.SqlServer);
        Assert.Equal("nvarchar(255)", noLen.Sql);
        Assert.NotEmpty(noLen.Warnings);

        var tooLong = TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.VarChar, Length: 5000), ArkDialect.SqlServer);
        Assert.Equal("nvarchar(max)", tooLong.Sql);
        Assert.NotEmpty(tooLong.Warnings);

        var ok = TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.VarChar, Length: 100), ArkDialect.SqlServer);
        Assert.Equal("nvarchar(100)", ok.Sql);
        Assert.Empty(ok.Warnings);
    }

    [Fact]
    public void Canonical_ToSqlServer_Binary_LengthBoundary()
    {
        Assert.Equal("varbinary(256)",
            TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.Binary, Length: 256), ArkDialect.SqlServer).Sql);
        Assert.Equal("varbinary(max)",
            TypeMapper.FromCanonical(CanonicalType.Blob, ArkDialect.SqlServer).Sql);
    }

    [Fact]
    public void SqlServer_FunctionTranslator_RecognizesAndEmits()
    {
        // GETDATE() → 识别 CurrentTimestamp → 目标输出 SYSDATETIME()
        Assert.Equal(ValueExprKind.CurrentTimestamp, FunctionTranslator.Recognize(ArkDialect.SqlServer, "getdate()"));
        Assert.Equal("SYSDATETIME()",
            FunctionTranslator.Emit(ArkDialect.SqlServer, ValueExprKind.CurrentTimestamp, "GETDATE()"));

        // NEWID() → UuidV4 → NEWID()
        Assert.Equal(ValueExprKind.UuidV4, FunctionTranslator.Recognize(ArkDialect.SqlServer, "newid()"));
        Assert.Equal("NEWID()", FunctionTranslator.Emit(ArkDialect.SqlServer, ValueExprKind.UuidV4, "newid()"));

        // ISNULL(a,b) → Coalesce → COALESCE(a, b)
        Assert.Equal(ValueExprKind.Coalesce, FunctionTranslator.Recognize(ArkDialect.SqlServer, "ISNULL(a, b)"));
        Assert.Equal("COALESCE(a, b)",
            FunctionTranslator.Emit(ArkDialect.SqlServer, ValueExprKind.Coalesce, "ISNULL(a, b)"));

        // 默认值元数据常带包裹括号：((0)) 识别为字面量
        Assert.Equal(ValueExprKind.Literal, FunctionTranslator.Recognize(ArkDialect.SqlServer, "((0))"));

        // 跨方言：PG now() → SQL Server SYSDATETIME()；MySQL uuid() → SQL Server NEWID()
        Assert.Equal("SYSDATETIME()",
            FunctionTranslator.Emit(ArkDialect.SqlServer, ValueExprKind.CurrentTimestamp, "now()"));
        Assert.Equal("NEWID()", FunctionTranslator.Emit(ArkDialect.SqlServer, ValueExprKind.UuidV4, "uuid()"));

        // SQL Server 拼接用 CONCAT 函数（无 ||）
        Assert.Equal("CONCAT(a, b)",
            FunctionTranslator.Emit(ArkDialect.SqlServer, ValueExprKind.Concat, "a || b"));
    }

    [Fact]
    public void SqlServer_Dialects_Get_ExposesSqlServerTraits()
    {
        var d = Dialects.Get(ArkDialect.SqlServer);
        Assert.Equal("", d.SkipExistingClause); // 无 ON CONFLICT，SkipExisting 在计划中警告
        Assert.Equal(2000, d.MaxBatchParams);   // SqlClient 上限 2100 留余量
        Assert.Equal('[', d.QuoteOpen);
        Assert.Equal(']', d.QuoteClose);
        Assert.True(d.SupportsSchemas);
        Assert.Equal("dbo", d.DefaultTargetSchema);
        Assert.True(d.BoolAsOneZero);
        Assert.Equal(BinaryLiteralFormat.SqlServerHex, d.BinaryFormat);
        Assert.Equal("[a].[b]", d.QuoteQualified(null, "a", "b"));
    }

    [Fact]
    public void SqlServer_QuoteIdentifier_DoublesClosingBracket()
    {
        var d = Dialects.Get(ArkDialect.SqlServer);
        Assert.Equal("[users]", d.QuoteIdentifier("users"));
        Assert.Equal("[we]]ird]", d.QuoteIdentifier("we]ird")); // 闭字符 ] 翻倍
    }
}
