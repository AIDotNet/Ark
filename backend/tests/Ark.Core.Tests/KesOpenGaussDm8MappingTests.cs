using Ark.Core.Metadata;
using Ark.Core.Typing;
using Xunit;

namespace Ark.Core.Tests;

/// <summary>
/// 人大金仓 / openGauss / 达梦 DM8 的类型双向映射与函数翻译测试。
/// 金仓/openGauss 与 PG 同款映射；DM8 走独立 DmToCanonical / Emit 分支。
/// </summary>
public class KesOpenGaussDm8MappingTests
{
    // ---------------- KingbaseES（PG 同款） ----------------

    [Fact]
    public void Kes_ToCanonical_MatchesPg()
    {
        Assert.Equal(CanonicalTypeId.Int32, TypeMapper.ToCanonical(ArkDialect.KingbaseES, "int4", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Int64, TypeMapper.ToCanonical(ArkDialect.KingbaseES, "int8", null, null, null).Id);
        var vc = TypeMapper.ToCanonical(ArkDialect.KingbaseES, "character varying(255)", null, null, null);
        Assert.Equal(CanonicalTypeId.VarChar, vc.Id);
        Assert.Equal(255, vc.Length);
        Assert.Equal(CanonicalTypeId.DateTimeOffset,
            TypeMapper.ToCanonical(ArkDialect.KingbaseES, "timestamp with time zone", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Binary, TypeMapper.ToCanonical(ArkDialect.KingbaseES, "bytea", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Guid, TypeMapper.ToCanonical(ArkDialect.KingbaseES, "uuid", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Json, TypeMapper.ToCanonical(ArkDialect.KingbaseES, "jsonb", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Boolean, TypeMapper.ToCanonical(ArkDialect.KingbaseES, "bool", null, null, null).Id);
    }

    [Fact]
    public void Kes_FromCanonical_MatchesPg()
    {
        Assert.Equal("boolean", TypeMapper.FromCanonical(CanonicalType.Bool, ArkDialect.KingbaseES).Sql);
        Assert.Equal("bigint", TypeMapper.FromCanonical(CanonicalType.Long, ArkDialect.KingbaseES).Sql);
        Assert.Equal("character varying(100)",
            TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.VarChar, Length: 100), ArkDialect.KingbaseES).Sql);
        Assert.Equal("uuid", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Guid), ArkDialect.KingbaseES).Sql);
        Assert.Equal("timestamp with time zone",
            TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTimeOffset), ArkDialect.KingbaseES).Sql);
        Assert.Equal("numeric(10,2)",
            TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.Decimal, Precision: 10, Scale: 2), ArkDialect.KingbaseES).Sql);
    }

    [Fact]
    public void Kes_FunctionTranslator_GenRandomUuid_RoundTrip()
    {
        // 识别：金仓 PG 模式默认值 gen_random_uuid() → UuidV4
        Assert.Equal(ValueExprKind.UuidV4, FunctionTranslator.Recognize(ArkDialect.KingbaseES, "gen_random_uuid()"));
        // Emit 回金仓 → gen_random_uuid()
        Assert.Equal("gen_random_uuid()",
            FunctionTranslator.Emit(ArkDialect.KingbaseES, ValueExprKind.UuidV4, "gen_random_uuid()"));
    }

    [Fact]
    public void Kes_FunctionTranslator_TimestampAndCoalesce()
    {
        Assert.Equal(ValueExprKind.CurrentTimestamp, FunctionTranslator.Recognize(ArkDialect.KingbaseES, "now()"));
        Assert.Equal("now()", FunctionTranslator.Emit(ArkDialect.KingbaseES, ValueExprKind.CurrentTimestamp, "now()"));
        Assert.Equal(ValueExprKind.Coalesce, FunctionTranslator.Recognize(ArkDialect.KingbaseES, "nvl(a, b)"));
        Assert.Equal("COALESCE(a, b)",
            FunctionTranslator.Emit(ArkDialect.KingbaseES, ValueExprKind.Coalesce, "nvl(a, b)"));
    }

    // ---------------- openGauss（PG 同款） ----------------

    [Fact]
    public void OpenGauss_ToCanonical_MatchesPg()
    {
        Assert.Equal(CanonicalTypeId.Int64, TypeMapper.ToCanonical(ArkDialect.OpenGauss, "int8", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Int16, TypeMapper.ToCanonical(ArkDialect.OpenGauss, "int2", null, null, null).Id);
        var num = TypeMapper.ToCanonical(ArkDialect.OpenGauss, "numeric(10,2)", null, null, null);
        Assert.Equal(CanonicalTypeId.Decimal, num.Id);
        Assert.Equal(10, num.Precision);
        Assert.Equal(2, num.Scale);
        Assert.Equal(CanonicalTypeId.DateTimeOffset,
            TypeMapper.ToCanonical(ArkDialect.OpenGauss, "timestamp with time zone", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Text, TypeMapper.ToCanonical(ArkDialect.OpenGauss, "text", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Json, TypeMapper.ToCanonical(ArkDialect.OpenGauss, "jsonb", null, null, null).Id);
    }

    [Fact]
    public void OpenGauss_FromCanonical_MatchesPg()
    {
        Assert.Equal("text", TypeMapper.FromCanonical(CanonicalType.Str, ArkDialect.OpenGauss).Sql);
        Assert.Equal("integer", TypeMapper.FromCanonical(CanonicalType.Int, ArkDialect.OpenGauss).Sql);
        Assert.Equal("timestamp without time zone",
            TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTime), ArkDialect.OpenGauss).Sql);
        Assert.Equal("bytea", TypeMapper.FromCanonical(CanonicalType.Blob, ArkDialect.OpenGauss).Sql);
        Assert.Equal("character varying(255)",
            TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.VarChar), ArkDialect.OpenGauss).Sql);
        // PG 系分支不做 decimal 精度截断
        Assert.Equal("numeric(80,40)",
            TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.Decimal, Precision: 80, Scale: 40), ArkDialect.OpenGauss).Sql);
    }

    [Fact]
    public void OpenGauss_FunctionTranslator_UuidAndTimestamp()
    {
        Assert.Equal(ValueExprKind.UuidV4, FunctionTranslator.Recognize(ArkDialect.OpenGauss, "gen_random_uuid()"));
        Assert.Equal("gen_random_uuid()",
            FunctionTranslator.Emit(ArkDialect.OpenGauss, ValueExprKind.UuidV4, "uuid_generate_v4()"));
        Assert.Equal(ValueExprKind.CurrentTimestamp, FunctionTranslator.Recognize(ArkDialect.OpenGauss, "current_timestamp"));
        Assert.Equal("now()", FunctionTranslator.Emit(ArkDialect.OpenGauss, ValueExprKind.CurrentTimestamp, "current_timestamp"));
        Assert.Equal(ValueExprKind.Concat, FunctionTranslator.Recognize(ArkDialect.OpenGauss, "a || b"));
        Assert.Equal("a || b", FunctionTranslator.Emit(ArkDialect.OpenGauss, ValueExprKind.Concat, "concat(a, b)"));
    }

    // ---------------- DM8（独立 Oracle 风格分支） ----------------

    [Fact]
    public void Dm8_ToCanonical_OracleStyleTypes()
    {
        var vc = TypeMapper.ToCanonical(ArkDialect.DM8, "VARCHAR2", 100, null, null);
        Assert.Equal(CanonicalTypeId.VarChar, vc.Id);
        Assert.Equal(100, vc.Length);

        var num = TypeMapper.ToCanonical(ArkDialect.DM8, "NUMBER", null, 10, 2);
        Assert.Equal(CanonicalTypeId.Decimal, num.Id);
        Assert.Equal(10, num.Precision);
        Assert.Equal(2, num.Scale);

        // DM 的 DATE 含时间部分（Oracle 语义）→ DateTime 而非 Date
        Assert.Equal(CanonicalTypeId.DateTime, TypeMapper.ToCanonical(ArkDialect.DM8, "DATE", null, null, null).Id);
        // TIMESTAMPTZ → DateTimeOffset
        Assert.Equal(CanonicalTypeId.DateTimeOffset, TypeMapper.ToCanonical(ArkDialect.DM8, "TIMESTAMPTZ", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Text, TypeMapper.ToCanonical(ArkDialect.DM8, "CLOB", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Binary, TypeMapper.ToCanonical(ArkDialect.DM8, "BLOB", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Boolean, TypeMapper.ToCanonical(ArkDialect.DM8, "BIT", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Single, TypeMapper.ToCanonical(ArkDialect.DM8, "BINARY_FLOAT", null, null, null).Id);
        Assert.Equal(CanonicalTypeId.Double, TypeMapper.ToCanonical(ArkDialect.DM8, "BINARY_DOUBLE", null, null, null).Id);
    }

    [Fact]
    public void Dm8_FromCanonical_OracleStyleTypes()
    {
        Assert.Equal("BIT", TypeMapper.FromCanonical(CanonicalType.Bool, ArkDialect.DM8).Sql);
        Assert.Equal("INTEGER", TypeMapper.FromCanonical(CanonicalType.Int, ArkDialect.DM8).Sql);
        Assert.Equal("BIGINT", TypeMapper.FromCanonical(CanonicalType.Long, ArkDialect.DM8).Sql);
        // 规范 Decimal 精度未声明 → NUMBER
        Assert.Equal("NUMBER", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Decimal), ArkDialect.DM8).Sql);
        Assert.Equal("TIMESTAMP(6)", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.DateTime), ArkDialect.DM8).Sql);
        Assert.Equal("CLOB", TypeMapper.FromCanonical(CanonicalType.Str, ArkDialect.DM8).Sql);
        // Guid 无原生类型 → VARCHAR(36) 文本 + 警告
        var guid = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Guid), ArkDialect.DM8);
        Assert.Equal("VARCHAR(36)", guid.Sql);
        Assert.NotEmpty(guid.Warnings);
        // 未声明长度的 VarChar → 默认 VARCHAR2(255) + 警告
        var vc = TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.VarChar), ArkDialect.DM8);
        Assert.Equal("VARCHAR2(255)", vc.Sql);
        Assert.NotEmpty(vc.Warnings);
        // 超长 VarChar → 降级 CLOB
        Assert.Equal("CLOB",
            TypeMapper.FromCanonical(new CanonicalType(CanonicalTypeId.VarChar, Length: 20000), ArkDialect.DM8).Sql);
        // DATE 含时间部分警告
        Assert.NotEmpty(TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Date), ArkDialect.DM8).Warnings);
    }

    [Fact]
    public void Dm8_FunctionTranslator_Sysdate_RoundTrip()
    {
        // 识别：SYSDATE / SYSTIMESTAMP → CurrentTimestamp；Emit 回 DM8 → SYSDATE
        Assert.Equal(ValueExprKind.CurrentTimestamp, FunctionTranslator.Recognize(ArkDialect.DM8, "SYSDATE"));
        Assert.Equal(ValueExprKind.CurrentTimestamp, FunctionTranslator.Recognize(ArkDialect.DM8, "systimestamp"));
        Assert.Equal("SYSDATE", FunctionTranslator.Emit(ArkDialect.DM8, ValueExprKind.CurrentTimestamp, "SYSDATE"));
    }

    [Fact]
    public void Dm8_FunctionTranslator_Nvl_Coalesce()
    {
        Assert.Equal(ValueExprKind.Coalesce, FunctionTranslator.Recognize(ArkDialect.DM8, "NVL(a, b)"));
        Assert.Equal("COALESCE(a, b)", FunctionTranslator.Emit(ArkDialect.DM8, ValueExprKind.Coalesce, "NVL(a, b)"));
        // DM 拼接用 ||
        Assert.Equal(ValueExprKind.Concat, FunctionTranslator.Recognize(ArkDialect.DM8, "'x' || col"));
        Assert.Equal("'x' || col", FunctionTranslator.Emit(ArkDialect.DM8, ValueExprKind.Concat, "'x' || col"));
    }

    [Fact]
    public void Dm8_FunctionTranslator_SysGuid_RecognizedButEmitFails()
    {
        // 识别为 UUID 语义
        Assert.Equal(ValueExprKind.UuidV4, FunctionTranslator.Recognize(ArkDialect.DM8, "SYS_GUID()"));
        // Emit 返回 null（DM8 无等效内置函数）→ 同步计划报错路径
        Assert.Null(FunctionTranslator.Emit(ArkDialect.DM8, ValueExprKind.UuidV4, "SYS_GUID()"));
    }

    // ---------------- 跨方言一致性 ----------------

    [Fact]
    public void KesAndOpenGauss_BehaveIdenticallyToPostgreSql()
    {
        foreach (var d in new[] { ArkDialect.PostgreSQL, ArkDialect.KingbaseES, ArkDialect.OpenGauss })
        {
            Assert.Equal("uuid", TypeMapper.FromCanonical(CanonicalType.Of(CanonicalTypeId.Guid), d).Sql);
            Assert.Equal(ValueExprKind.UuidV4, FunctionTranslator.Recognize(d, "gen_random_uuid()"));
            Assert.Equal("gen_random_uuid()", FunctionTranslator.Emit(d, ValueExprKind.UuidV4, "gen_random_uuid()"));
        }
    }

    [Fact]
    public void Dm8_NumberRoundTrip_DocumentsDecimalEmission()
    {
        // NUMBER(10,2) → 规范 Decimal(10,2) → 目标 DM8 输出 decimal(10,2)（DM 的 DECIMAL 与 NUMBER 同义）
        var canonical = TypeMapper.ToCanonical(ArkDialect.DM8, "NUMBER", null, 10, 2);
        Assert.Equal((10, 2), (canonical.Precision, canonical.Scale));
        Assert.Equal("decimal(10,2)", TypeMapper.FromCanonical(canonical, ArkDialect.DM8).Sql);
    }
}
