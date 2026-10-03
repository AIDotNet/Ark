using Ark.Core.Metadata;
using Ark.Core.Typing;
using Xunit;

namespace Ark.Core.Tests;

public class TypeMapperTests
{
    // ---------- provider 类型 → 规范类型 ----------

    [Theory]
    [InlineData("int4", CanonicalTypeId.Int32)]
    [InlineData("int8", CanonicalTypeId.Int64)]
    [InlineData("numeric", CanonicalTypeId.Decimal)]
    [InlineData("character varying", CanonicalTypeId.VarChar)]
    [InlineData("text", CanonicalTypeId.Text)]
    [InlineData("timestamp with time zone", CanonicalTypeId.DateTimeOffset)]
    [InlineData("uuid", CanonicalTypeId.Guid)]
    [InlineData("jsonb", CanonicalTypeId.Json)]
    [InlineData("bytea", CanonicalTypeId.Binary)]
    [InlineData("bool", CanonicalTypeId.Boolean)]
    public void PgTypes_MapToCanonical(string input, CanonicalTypeId expected)
    {
        var t = TypeMapper.ToCanonical(ArkDialect.PostgreSQL, input, null, null, null);
        Assert.Equal(expected, t.Id);
    }

    [Theory]
    [InlineData("tinyint(1)", CanonicalTypeId.Boolean)]
    [InlineData("tinyint(4)", CanonicalTypeId.Int16)]
    [InlineData("bigint", CanonicalTypeId.Int64)]
    [InlineData("varchar(50)", CanonicalTypeId.VarChar)]
    [InlineData("datetime", CanonicalTypeId.DateTime)]
    [InlineData("json", CanonicalTypeId.Json)]
    [InlineData("longblob", CanonicalTypeId.Binary)]
    public void MySqlTypes_MapToCanonical(string input, CanonicalTypeId expected)
    {
        var t = TypeMapper.ToCanonical(ArkDialect.MySQL, input, null, null, null);
        Assert.Equal(expected, t.Id);
    }

    [Fact]
    public void MySqlVarChar_ParsesLength()
    {
        var t = TypeMapper.ToCanonical(ArkDialect.MySQL, "varchar(120)", null, null, null);
        Assert.Equal(120, t.Length);
    }

    [Theory]
    [InlineData("INTEGER", CanonicalTypeId.Int64)]
    [InlineData("TEXT", CanonicalTypeId.Text)]
    [InlineData("REAL", CanonicalTypeId.Double)]
    [InlineData("BLOB", CanonicalTypeId.Binary)]
    [InlineData("BOOLEAN", CanonicalTypeId.Boolean)]
    public void SqliteDeclaredTypes_MapToCanonical(string input, CanonicalTypeId expected)
    {
        var t = TypeMapper.ToCanonical(ArkDialect.SQLite, input, null, null, null);
        Assert.Equal(expected, t.Id);
    }

    // ---------- 规范类型 → 目标方言 ----------

    [Fact]
    public void Guid_ToPg_IsUuid_ToMySql_IsChar36_ToSqlite_IsText()
    {
        var g = CanonicalType.Of(CanonicalTypeId.Guid);
        Assert.Equal("uuid", TypeMapper.FromCanonical(g, ArkDialect.PostgreSQL).Sql);
        Assert.Equal("char(36)", TypeMapper.FromCanonical(g, ArkDialect.MySQL).Sql);
        Assert.Equal("TEXT", TypeMapper.FromCanonical(g, ArkDialect.SQLite).Sql);
    }

    [Fact]
    public void Decimal_ToMySql_CapsPrecision()
    {
        var d = new CanonicalType(CanonicalTypeId.Decimal, Precision: 80, Scale: 40);
        var m = TypeMapper.FromCanonical(d, ArkDialect.MySQL);
        Assert.Contains("decimal(65,30)", m.Sql);
        Assert.NotEmpty(m.Warnings);
    }

    [Fact]
    public void VarChar_OverMySqlLimit_FallsBackToText()
    {
        var v = new CanonicalType(CanonicalTypeId.VarChar, Length: 100000);
        var m = TypeMapper.FromCanonical(v, ArkDialect.MySQL);
        Assert.Equal("text", m.Sql);
        Assert.NotEmpty(m.Warnings);
    }

    [Fact]
    public void Boolean_ToMySql_WarnsTinyint()
    {
        var m = TypeMapper.FromCanonical(CanonicalType.Bool, ArkDialect.MySQL);
        Assert.Equal("tinyint(1)", m.Sql);
        Assert.NotEmpty(m.Warnings);
    }

    [Fact]
    public void DateTimeOffset_ToMySql_LosesTz()
    {
        var m = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTimeOffset), ArkDialect.MySQL);
        Assert.Equal("datetime", m.Sql);
        Assert.Contains(m.Warnings, w => w.Contains("时区"));
    }
}
