using Ark.Core.Metadata;

namespace Ark.Core.Typing;

/// <summary>类型映射结果：目标方言的 SQL 类型 + 降级/截断警告。</summary>
public sealed record TypeMapping(string Sql, IReadOnlyList<string> Warnings)
{
    public static TypeMapping Ok(string sql) => new(sql, []);

    public static TypeMapping Warn(string sql, string warning) => new(sql, [warning]);
}

/// <summary>
/// 以“规范类型”为中枢的三库类型映射：
/// ① provider 类型 → 规范类型（读元数据时）；② 规范类型 → 目标方言 SQL（同步/设计器时）。
/// 无法精确映射时按“降级 + 警告”处理，绝不静默失败。
/// </summary>
public static class TypeMapper
{
    // ------------------------------------------------------------------
    // ① provider 类型 → Canonical
    // ------------------------------------------------------------------

    public static CanonicalType ToCanonical(ArkDialect dialect, string typeName, int? length, int? precision, int? scale)
    {
        var name = (typeName ?? "").Trim().ToLowerInvariant();
        return dialect switch
        {
            ArkDialect.PostgreSQL => PgToCanonical(name, length, precision, scale),
            ArkDialect.MySQL => MyToCanonical(name, length, precision, scale),
            ArkDialect.SQLite => SqToCanonical(name),
            ArkDialect.MariaDB => MyToCanonical(name, length, precision, scale),
            ArkDialect.SqlServer => MsToCanonical(name, length, precision, scale),
            ArkDialect.ClickHouse => ChToCanonical(name, length, precision, scale),
            ArkDialect.DuckDB => DkToCanonical(name, length, precision, scale),
            ArkDialect.DM8 => DmToCanonical(name, length, precision, scale),
            ArkDialect.KingbaseES or ArkDialect.OpenGauss => PgToCanonical(name, length, precision, scale),
            _ => new CanonicalType(CanonicalTypeId.Unknown),
        };
    }

    private static CanonicalType PgToCanonical(string name, int? len, int? prec, int? scale)
    {
        // format_type 可能给出 "character varying(255)" / "numeric(10,2)" / "timestamp(3) with time zone" / "_text"
        var bare = name.Replace("[]", "").Trim();
        if (name.EndsWith("[]") || name.StartsWith('_'))
            return CanonicalType.Of(CanonicalTypeId.Json); // 数组降级，警告由调用方基于名称给出
        // 解析括号参数：numeric(10,2) → prec/scale；character varying(255) → 长度
        int? argLen = null, argPrec = null, argScale = null;
        var paren = bare.IndexOf('(');
        if (paren >= 0)
        {
            var close = bare.IndexOf(')', paren);
            if (close > paren)
            {
                var parts = bare[(paren + 1)..close].Split(',');
                if (parts.Length == 1 && int.TryParse(parts[0], out var a1)) argLen = a1;
                if (parts.Length == 2 && int.TryParse(parts[0], out var p1) && int.TryParse(parts[1], out var s1))
                {
                    argPrec = p1;
                    argScale = s1;
                }
                bare = bare[..paren].Trim();
            }
        }
        if (bare.Contains("character varying") || bare == "varchar") return new CanonicalType(CanonicalTypeId.VarChar, len ?? argLen);
        if (bare.StartsWith("character") || bare == "bpchar" || bare == "char") return new CanonicalType(CanonicalTypeId.Char, len ?? argLen);
        if (bare == "text" || bare == "citext") return CanonicalType.Str;
        if (bare == "smallint" || bare == "int2") return CanonicalType.Of(CanonicalTypeId.Int16);
        if (bare == "integer" || bare == "int4" || bare == "int") return CanonicalType.Int;
        if (bare == "bigint" || bare == "int8") return CanonicalType.Long;
        if (bare == "numeric" || bare == "decimal") return new CanonicalType(CanonicalTypeId.Decimal,
            Precision: prec ?? argPrec, Scale: scale ?? argScale);
        if (bare == "real" || bare == "float4") return CanonicalType.Of(CanonicalTypeId.Single);
        if (bare == "double precision" || bare == "float8") return CanonicalType.Of(CanonicalTypeId.Double);
        if (bare == "boolean" || bare == "bool") return CanonicalType.Bool;
        if (bare == "date") return CanonicalType.Of(CanonicalTypeId.Date);
        if (bare.StartsWith("timestamp") && bare.Contains("with time zone")) return CanonicalType.Of(CanonicalTypeId.DateTimeOffset);
        if (bare.StartsWith("timestamp")) return CanonicalType.Of(CanonicalTypeId.DateTime);
        if (bare.StartsWith("time")) return CanonicalType.Of(CanonicalTypeId.Time);
        if (bare == "bytea") return CanonicalType.Blob;
        if (bare == "uuid") return CanonicalType.Of(CanonicalTypeId.Guid);
        if (bare == "json" || bare == "jsonb") return CanonicalType.Of(CanonicalTypeId.Json);
        if (bare == "xml") return CanonicalType.Of(CanonicalTypeId.Xml);
        if (bare == "money") return new CanonicalType(CanonicalTypeId.Decimal, Precision: 19, Scale: 4);
        return CanonicalType.Of(CanonicalTypeId.Unknown);
    }

    private static CanonicalType MyToCanonical(string name, int? len, int? prec, int? scale)
    {
        var bare = name;
        var paren = bare.IndexOf('(');
        int? argLen = null, argPrec = null, argScale = null;
        if (paren >= 0)
        {
            var close = name.IndexOf(')', paren);
            if (close > paren)
            {
                var arg = name[(paren + 1)..close].Trim();
                bare = name[..paren].Trim();
                var parts = arg.Split(',');
                if (parts.Length == 1 && int.TryParse(parts[0], out var a1)) argLen = a1;
                if (parts.Length == 2 && int.TryParse(parts[0], out var p1) && int.TryParse(parts[1], out var s1))
                {
                    argPrec = p1;
                    argScale = s1;
                }
            }
        }
        var isBoolTinyint = bare == "tinyint" && argLen == 1;

        return bare switch
        {
            "tinyint" => isBoolTinyint ? CanonicalType.Bool : CanonicalType.Of(CanonicalTypeId.Int16),
            "smallint" => CanonicalType.Of(CanonicalTypeId.Int16),
            "mediumint" => CanonicalType.Int,
            "int" or "integer" => CanonicalType.Int,
            "bigint" => CanonicalType.Long,
            "decimal" or "numeric" => new CanonicalType(CanonicalTypeId.Decimal,
                Precision: prec ?? argPrec, Scale: scale ?? argScale),
            "float" => CanonicalType.Of(CanonicalTypeId.Single),
            "double" or "real" => CanonicalType.Of(CanonicalTypeId.Double),
            "bit" => (len ?? argLen) == 1 ? CanonicalType.Bool : CanonicalType.Blob,
            "char" => new CanonicalType(CanonicalTypeId.Char, len ?? argLen),
            "varchar" => new CanonicalType(CanonicalTypeId.VarChar, len ?? argLen),
            "tinytext" or "text" or "mediumtext" or "longtext" => CanonicalType.Str,
            "date" => CanonicalType.Of(CanonicalTypeId.Date),
            "datetime" => CanonicalType.Of(CanonicalTypeId.DateTime),
            "timestamp" => CanonicalType.Of(CanonicalTypeId.DateTime), // 时区语义差异由调用方提示
            "time" => CanonicalType.Of(CanonicalTypeId.Time),
            "year" => CanonicalType.Of(CanonicalTypeId.Int16),
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => CanonicalType.Blob,
            "json" => CanonicalType.Of(CanonicalTypeId.Json),
            "uuid" => CanonicalType.Of(CanonicalTypeId.Guid), // MariaDB 10.7+ 原生 uuid 类型（MySQL 无此类型，出现时按 Guid 处理）
            "enum" or "set" => new CanonicalType(CanonicalTypeId.VarChar, 255),
            _ => CanonicalType.Of(CanonicalTypeId.Unknown),
        };
    }

    private static CanonicalType SqToCanonical(string declared)
    {
        var t = declared ?? "";
        if (string.IsNullOrWhiteSpace(t)) return CanonicalType.Blob; // 无声明类型 → BLOB 亲和
        var u = t.ToUpperInvariant();
        // 解析括号参数：VARCHAR(50) → 长度；DECIMAL(10,2) → 精度/小数位（不解析则读回降级为无参类型，PUT 时变空 diff）
        int? argLen = null, argPrec = null, argScale = null;
        var paren = u.IndexOf('(');
        if (paren >= 0)
        {
            var close = u.IndexOf(')', paren);
            if (close > paren)
            {
                var parts = u[(paren + 1)..close].Split(',');
                if (parts.Length == 1 && int.TryParse(parts[0], out var a1)) argLen = a1;
                if (parts.Length == 2 && int.TryParse(parts[0], out var p1) && int.TryParse(parts[1], out var s1))
                {
                    argPrec = p1;
                    argScale = s1;
                }
                u = u[..paren].Trim();
            }
        }
        if (u.Contains("BOOL")) return CanonicalType.Bool;
        if (u.Contains("GUID") || u.Contains("UUID")) return CanonicalType.Of(CanonicalTypeId.Guid);
        if (u.Contains("INT")) return CanonicalType.Long;
        if (u.Contains("DEC") || u.Contains("NUM"))
            return new CanonicalType(CanonicalTypeId.Decimal, Precision: argPrec ?? argLen, Scale: argScale);
        if (u.Contains("CLOB") || u.Contains("TEXT")) return CanonicalType.Str;
        if (u.Contains("CHAR"))
        {
            // VARCHAR(n) → VarChar(n)；CHAR(n) → Char(n)；无参数保持原降级（Text），保证原样重提交不产生虚假 diff
            if (u.Contains("VAR")) return new CanonicalType(CanonicalTypeId.VarChar, argLen);
            return argLen is null ? CanonicalType.Str : new CanonicalType(CanonicalTypeId.Char, argLen);
        }
        if (u.Contains("BLOB")) return new CanonicalType(CanonicalTypeId.Binary, argLen);
        if (u.Contains("REAL") || u.Contains("FLOA") || u.Contains("DOUB")) return CanonicalType.Of(CanonicalTypeId.Double);
        if (u.Contains("DATE") || u.Contains("TIME")) return CanonicalType.Of(CanonicalTypeId.DateTime);
        return CanonicalType.Str; // 其余按 NUMERIC/TEXT 亲和的保守值
    }

    /// <summary>SQL Server 类型名 → Canonical（information_schema/DMV 均为小写类型名）。</summary>
    private static CanonicalType MsToCanonical(string name, int? len, int? prec, int? scale)
    {
        var bare = name;
        var paren = bare.IndexOf('(');
        int? argLen = null, argPrec = null, argScale = null;
        if (paren >= 0)
        {
            var close = name.IndexOf(')', paren);
            if (close > paren)
            {
                var parts = name[(paren + 1)..close].Split(',');
                bare = name[..paren].Trim();
                if (parts.Length == 1)
                {
                    var a = parts[0].Trim();
                    if (string.Equals(a, "max", StringComparison.OrdinalIgnoreCase)) argLen = -1; // max 标记
                    else if (int.TryParse(a, out var a1)) argLen = a1;
                }
                if (parts.Length == 2 && int.TryParse(parts[0], out var p1) && int.TryParse(parts[1], out var s1))
                {
                    argPrec = p1;
                    argScale = s1;
                }
            }
        }
        var isMax = argLen == -1;
        return bare switch
        {
            "bit" => CanonicalType.Bool,
            "tinyint" => CanonicalType.Of(CanonicalTypeId.Int16), // 无符号 0-255，加宽无信息损失
            "smallint" => CanonicalType.Of(CanonicalTypeId.Int16),
            "int" => CanonicalType.Int,
            "bigint" => CanonicalType.Long,
            "decimal" or "numeric" or "money" or "smallmoney" => new CanonicalType(CanonicalTypeId.Decimal,
                Precision: prec ?? argPrec ?? (bare.Contains("money") ? 19 : null),
                Scale: scale ?? argScale ?? (bare == "money" ? 4 : bare == "smallmoney" ? 4 : null)),
            "float" => CanonicalType.Of(CanonicalTypeId.Double),
            "real" => CanonicalType.Of(CanonicalTypeId.Single),
            "char" or "nchar" => new CanonicalType(CanonicalTypeId.Char, isMax ? null : len ?? argLen),
            "varchar" or "nvarchar" => isMax
                ? CanonicalType.Str
                : new CanonicalType(CanonicalTypeId.VarChar, len ?? argLen),
            "text" or "ntext" => CanonicalType.Str,
            "date" => CanonicalType.Of(CanonicalTypeId.Date),
            "time" => CanonicalType.Of(CanonicalTypeId.Time),
            "datetime" or "datetime2" or "smalldatetime" => CanonicalType.Of(CanonicalTypeId.DateTime),
            "datetimeoffset" => CanonicalType.Of(CanonicalTypeId.DateTimeOffset),
            "binary" or "varbinary" or "image" => isMax ? CanonicalType.Blob : new CanonicalType(CanonicalTypeId.Binary, len ?? argLen),
            "uniqueidentifier" => CanonicalType.Of(CanonicalTypeId.Guid),
            "xml" => CanonicalType.Of(CanonicalTypeId.Xml),
            // sql_variant / hierarchyid / geography / geometry / sysname 等 → 未识别，调用方按文本降级并警告
            _ => CanonicalType.Of(CanonicalTypeId.Unknown),
        };
    }

    /// <summary>ClickHouse 类型名 → Canonical（Nullable/LowCardinality 已由 Provider 解包）。</summary>
    private static CanonicalType ChToCanonical(string name, int? len, int? prec, int? scale)
    {
        var bare = name;
        var paren = bare.IndexOf('(');
        int? argLen = null, argPrec = null, argScale = null;
        if (paren >= 0)
        {
            var close = name.IndexOf(')', paren);
            if (close > paren)
            {
                var inner = name[(paren + 1)..close].Trim();
                var parts = inner.Split(',');
                bare = name[..paren].Trim();
                if (parts.Length == 1 && int.TryParse(parts[0], out var a1)) argLen = a1;
                if (parts.Length == 2 && int.TryParse(parts[0], out var p1) && int.TryParse(parts[1], out var s1))
                {
                    argPrec = p1;
                    argScale = s1;
                }
            }
        }
        return bare switch
        {
            "int8" => CanonicalType.Of(CanonicalTypeId.Int16),
            "int16" => CanonicalType.Of(CanonicalTypeId.Int16),
            "int32" => CanonicalType.Int,
            "int64" => CanonicalType.Long,
            // 无符号类型向上有符号加宽；uint64 超出 int64 表示范围 → Decimal(20,0) 保留数值
            "uint8" => CanonicalType.Of(CanonicalTypeId.Int16),
            "uint16" => CanonicalType.Int,
            "uint32" => CanonicalType.Long,
            "uint64" => new CanonicalType(CanonicalTypeId.Decimal, Precision: 20, Scale: 0),
            "float32" => CanonicalType.Of(CanonicalTypeId.Single),
            "float64" => CanonicalType.Of(CanonicalTypeId.Double),
            "decimal" => new CanonicalType(CanonicalTypeId.Decimal,
                Precision: prec ?? argPrec, Scale: scale ?? argScale),
            "string" => CanonicalType.Str,
            "fixedstring" => new CanonicalType(CanonicalTypeId.Char, argLen),
            "uuid" => CanonicalType.Of(CanonicalTypeId.Guid),
            "date" or "date32" => CanonicalType.Of(CanonicalTypeId.Date),
            "datetime" => CanonicalType.Of(CanonicalTypeId.DateTime),
            "datetime64" => CanonicalType.Of(CanonicalTypeId.DateTime),
            "ipv4" or "ipv6" => new CanonicalType(CanonicalTypeId.VarChar, 45),
            "enum8" or "enum16" => new CanonicalType(CanonicalTypeId.VarChar, 255),
            "json" => CanonicalType.Of(CanonicalTypeId.Json),
            // array/map/tuple/nested 等复合类型 → JSON 文本（Provider 读取时 CAST 为字符串并警告）
            "array" or "map" or "tuple" or "nested" => CanonicalType.Of(CanonicalTypeId.Json),
            _ => CanonicalType.Of(CanonicalTypeId.Unknown),
        };
    }

    /// <summary>DuckDB 类型名 → Canonical。</summary>
    private static CanonicalType DkToCanonical(string name, int? len, int? prec, int? scale)
    {
        var bare = name;
        var paren = bare.IndexOf('(');
        int? argLen = null, argPrec = null, argScale = null;
        if (paren >= 0)
        {
            var close = name.IndexOf(')', paren);
            if (close > paren)
            {
                var parts = name[(paren + 1)..close].Split(',');
                bare = name[..paren].Trim();
                if (parts.Length == 1 && int.TryParse(parts[0], out var a1)) argLen = a1;
                if (parts.Length == 2 && int.TryParse(parts[0], out var p1) && int.TryParse(parts[1], out var s1))
                {
                    argPrec = p1;
                    argScale = s1;
                }
            }
        }
        return bare switch
        {
            "boolean" or "bool" => CanonicalType.Bool,
            "tinyint" => CanonicalType.Of(CanonicalTypeId.Int16), // DuckDB tinyint 为有符号 int8
            "smallint" => CanonicalType.Of(CanonicalTypeId.Int16),
            "integer" or "int" or "int32" => CanonicalType.Int,
            "bigint" or "int64" => CanonicalType.Long,
            "hugeint" => new CanonicalType(CanonicalTypeId.Decimal, Precision: 38, Scale: 0),
            "utinyint" => CanonicalType.Of(CanonicalTypeId.Int16),
            "usmallint" => CanonicalType.Int,
            "uinteger" or "uint" or "uint32" => CanonicalType.Long,
            "ubigint" => new CanonicalType(CanonicalTypeId.Decimal, Precision: 20, Scale: 0),
            "decimal" or "numeric" => new CanonicalType(CanonicalTypeId.Decimal,
                Precision: prec ?? argPrec, Scale: scale ?? argScale),
            "float" or "float4" or "real" => CanonicalType.Of(CanonicalTypeId.Single),
            "double" or "float8" => CanonicalType.Of(CanonicalTypeId.Double),
            "char" or "bpchar" => new CanonicalType(CanonicalTypeId.Char, len ?? argLen),
            "varchar" => new CanonicalType(CanonicalTypeId.VarChar, len ?? argLen),
            "text" or "string" => CanonicalType.Str,
            "blob" or "bytea" => CanonicalType.Blob,
            "date" => CanonicalType.Of(CanonicalTypeId.Date),
            "time" => CanonicalType.Of(CanonicalTypeId.Time),
            "timestamp" or "datetime" => CanonicalType.Of(CanonicalTypeId.DateTime),
            "timestamptz" or "timestamp with time zone" => CanonicalType.Of(CanonicalTypeId.DateTimeOffset),
            "uuid" => CanonicalType.Of(CanonicalTypeId.Guid),
            "json" => CanonicalType.Of(CanonicalTypeId.Json),
            // list/struct/map/union/enum 等复合类型 → JSON 文本（Provider 读取时 CAST 并警告）
            "list" or "struct" or "map" or "union" or "enum" or "array" => CanonicalType.Of(CanonicalTypeId.Json),
            _ => CanonicalType.Of(CanonicalTypeId.Unknown),
        };
    }

    /// <summary>达梦 DM8 类型名 → Canonical（Oracle 风格；系统视图返回大写，统一转小写比较）。</summary>
    private static CanonicalType DmToCanonical(string name, int? len, int? prec, int? scale)
    {
        var bare = (name ?? "").Trim().ToLowerInvariant();
        var paren = bare.IndexOf('(');
        int? argLen = null, argPrec = null, argScale = null;
        if (paren >= 0)
        {
            var close = bare.IndexOf(')', paren);
            if (close > paren)
            {
                var parts = bare[(paren + 1)..close].Split(',');
                bare = bare[..paren].Trim();
                if (parts.Length == 1 && int.TryParse(parts[0], out var a1)) argLen = a1;
                if (parts.Length == 2 && int.TryParse(parts[0], out var p1) && int.TryParse(parts[1], out var s1))
                {
                    argPrec = p1;
                    argScale = s1;
                }
            }
        }
        return bare switch
        {
            "bit" => CanonicalType.Bool,
            "tinyint" => CanonicalType.Of(CanonicalTypeId.Int16),
            "smallint" => CanonicalType.Of(CanonicalTypeId.Int16),
            "int" or "integer" or "pls_integer" or "binary_integer" => CanonicalType.Int,
            "bigint" => CanonicalType.Long,
            "number" or "numeric" or "decimal" or "dec" => new CanonicalType(CanonicalTypeId.Decimal,
                Precision: prec ?? argPrec, Scale: scale ?? argScale),
            "binary_float" => CanonicalType.Of(CanonicalTypeId.Single),
            "binary_double" or "float" or "double precision" => CanonicalType.Of(CanonicalTypeId.Double),
            "char" or "nchar" or "character" => new CanonicalType(CanonicalTypeId.Char, len ?? argLen),
            "varchar" or "varchar2" or "nvarchar" or "nvarchar2" => new CanonicalType(CanonicalTypeId.VarChar, len ?? argLen),
            "clob" or "nclob" or "text" or "long" or "longvarchar" => CanonicalType.Str,
            "blob" or "raw" or "long raw" or "varbinary" or "binary" or "image" => CanonicalType.Blob,
            // DM 的 DATE 含时间部分（Oracle 语义）
            "date" => CanonicalType.Of(CanonicalTypeId.DateTime),
            "time" => CanonicalType.Of(CanonicalTypeId.Time),
            "timestamp" or "datetime" => CanonicalType.Of(CanonicalTypeId.DateTime),
            "timestamp with time zone" or "timestamp with local time zone" or "timestamptz"
                => CanonicalType.Of(CanonicalTypeId.DateTimeOffset),
            "interval year" or "interval day" => CanonicalType.Of(CanonicalTypeId.Unknown),
            "xmltype" => CanonicalType.Of(CanonicalTypeId.Xml),
            _ => CanonicalType.Of(CanonicalTypeId.Unknown),
        };
    }

    // ------------------------------------------------------------------
    // ② Canonical → 目标方言 SQL
    // ------------------------------------------------------------------

    /// <summary>
    /// PostgreSQL 参数 cast 后缀（jsonb/timestamp 等不接受未知文本参数）；非生成列返回 null 表示无需。
    /// 优先用列的原始类型名（NativeType）：数组等类型在规范模型中降级（smallint[]→Json），
    /// 按规范类型反推会得到错误的 ::jsonb（42804）。
    /// </summary>
    public static string? PgCastFor(CanonicalColumn col)
    {
        if (col.IsGenerated) return null;
        if (!string.IsNullOrWhiteSpace(col.NativeType)) return col.NativeType;
        return FromCanonical(col.Type, ArkDialect.PostgreSQL).Sql;
    }

    public static TypeMapping FromCanonical(CanonicalType type, ArkDialect target)
    {
        var (sql, warns) = Emit(type, target);
        return new TypeMapping(sql, warns);
    }

    private static (string Sql, string[] Warnings) Emit(CanonicalType t, ArkDialect d)
    {
        string[] W(string s) => [s];
        switch (d)
        {
            case ArkDialect.PostgreSQL or ArkDialect.KingbaseES or ArkDialect.OpenGauss:
                return t.Id switch
                {
                    CanonicalTypeId.Boolean => ("boolean", []),
                    CanonicalTypeId.Int16 => ("smallint", []),
                    CanonicalTypeId.Int32 => ("integer", []),
                    CanonicalTypeId.Int64 => ("bigint", []),
                    CanonicalTypeId.Decimal => t.Precision is null
                        ? ("numeric", [])
                        : ($"numeric({t.Precision},{t.Scale ?? 0})", []),
                    CanonicalTypeId.Single => ("real", []),
                    CanonicalTypeId.Double => ("double precision", []),
                    CanonicalTypeId.Char => ($"character({t.Length ?? 1})", []),
                    CanonicalTypeId.VarChar => t.Length is null
                        ? ("character varying(255)", W("变长字符串未声明长度，默认为 character varying(255)"))
                        : ($"character varying({t.Length})", []),
                    CanonicalTypeId.Text => ("text", []),
                    CanonicalTypeId.Date => ("date", []),
                    CanonicalTypeId.Time => ("time without time zone", []),
                    CanonicalTypeId.DateTime => ("timestamp without time zone", []),
                    CanonicalTypeId.DateTimeOffset => ("timestamp with time zone", []),
                    CanonicalTypeId.Binary => ("bytea", []),
                    CanonicalTypeId.Guid => ("uuid", []),
                    CanonicalTypeId.Json => ("jsonb", []),
                    CanonicalTypeId.Xml => ("xml", []),
                    _ => ("text", W($"未识别的类型 {t.Id}，已降级为 text")),
                };

            case ArkDialect.MySQL or ArkDialect.MariaDB:
                return t.Id switch
                {
                    CanonicalTypeId.Boolean => ("tinyint(1)", W("布尔类型映射为 tinyint(1)")),
                    CanonicalTypeId.Int16 => ("smallint", []),
                    CanonicalTypeId.Int32 => ("int", []),
                    CanonicalTypeId.Int64 => ("bigint", []),
                    CanonicalTypeId.Decimal => DecimalForMySql(t),
                    CanonicalTypeId.Single => ("float", []),
                    CanonicalTypeId.Double => ("double", []),
                    CanonicalTypeId.Char => ($"char({t.Length ?? 1})", []),
                    CanonicalTypeId.VarChar => VarCharForMySql(t),
                    CanonicalTypeId.Text => ("text", []),
                    CanonicalTypeId.Date => ("date", []),
                    CanonicalTypeId.Time => ("time", []),
                    CanonicalTypeId.DateTime => ("datetime", []),
                    CanonicalTypeId.DateTimeOffset => ("datetime", W("MySQL 无时区感知类型，DateTimeOffset 将丢失时区语义")),
                    CanonicalTypeId.Binary => t.Length is null
                        ? ("longblob", [])
                        : (t.Length <= 65535 ? $"varbinary({t.Length})" : "longblob",
                           t.Length <= 65535 ? [] : W("二进制长度超过 varbinary 上限，已降级为 longblob")),
                    CanonicalTypeId.Guid => ("char(36)", []),
                    CanonicalTypeId.Json => ("json", []),
                    CanonicalTypeId.Xml => ("longtext", W("XML 以 longtext 存储")),
                    _ => ("text", W($"未识别的类型 {t.Id}，已降级为 text")),
                };

            case ArkDialect.SqlServer:
                return t.Id switch
                {
                    CanonicalTypeId.Boolean => ("bit", W("布尔类型映射为 bit(0/1)")),
                    CanonicalTypeId.Int16 => ("smallint", []),
                    CanonicalTypeId.Int32 => ("int", []),
                    CanonicalTypeId.Int64 => ("bigint", []),
                    CanonicalTypeId.Decimal => DecimalForClamp(t, 38, "SQL Server"),
                    CanonicalTypeId.Single => ("real", []),
                    CanonicalTypeId.Double => ("float", []),
                    CanonicalTypeId.Char => ($"nchar({t.Length ?? 1})", []),
                    CanonicalTypeId.VarChar => VarCharForSqlServer(t),
                    CanonicalTypeId.Text => ("nvarchar(max)", []),
                    CanonicalTypeId.Date => ("date", []),
                    CanonicalTypeId.Time => ("time", []),
                    CanonicalTypeId.DateTime => ("datetime2", []),
                    CanonicalTypeId.DateTimeOffset => ("datetimeoffset", []),
                    CanonicalTypeId.Binary => t.Length is null || t.Length > 8000
                        ? ("varbinary(max)", [])
                        : ($"varbinary({t.Length})", []),
                    CanonicalTypeId.Guid => ("uniqueidentifier", []),
                    CanonicalTypeId.Json => ("nvarchar(max)", W("JSON 以 nvarchar(max) 文本存储")),
                    CanonicalTypeId.Xml => ("xml", []),
                    _ => ("nvarchar(max)", W($"未识别的类型 {t.Id}，已降级为 nvarchar(max)")),
                };

            case ArkDialect.ClickHouse:
                return t.Id switch
                {
                    CanonicalTypeId.Boolean => ("UInt8", W("布尔类型映射为 UInt8(0/1)")),
                    CanonicalTypeId.Int16 => ("Int16", []),
                    CanonicalTypeId.Int32 => ("Int32", []),
                    CanonicalTypeId.Int64 => ("Int64", []),
                    CanonicalTypeId.Decimal => DecimalForClamp(t, 76, "ClickHouse"),
                    CanonicalTypeId.Single => ("Float32", []),
                    CanonicalTypeId.Double => ("Float64", []),
                    CanonicalTypeId.Char => ($"FixedString({t.Length ?? 1})",
                        W("FixedString 以 \\0 填充定长，字符串较短时读取会带填充符，请人工确认")),
                    CanonicalTypeId.VarChar => t.Length is null
                        ? ("String", [])
                        : ("String", W("ClickHouse String 不限长，声明的长度将被忽略")),
                    CanonicalTypeId.Text => ("String", []),
                    CanonicalTypeId.Date => ("Date", []),
                    CanonicalTypeId.Time => ("String", W("ClickHouse 无时刻类型，已降级为 String")),
                    CanonicalTypeId.DateTime => ("DateTime", []),
                    CanonicalTypeId.DateTimeOffset => ("DateTime", W("ClickHouse DateTime 为列级时区，时区语义请人工确认")),
                    CanonicalTypeId.Binary => ("String", W("二进制以 String 存储，需应用层 hex/unhex 编解码")),
                    CanonicalTypeId.Guid => ("UUID", []),
                    CanonicalTypeId.Json => ("String", W("JSON 以 String 存储（JSON 类型为实验特性）")),
                    CanonicalTypeId.Xml => ("String", W("XML 以 String 存储")),
                    _ => ("String", W($"未识别的类型 {t.Id}，已降级为 String")),
                };

            case ArkDialect.DuckDB:
                return t.Id switch
                {
                    CanonicalTypeId.Boolean => ("BOOLEAN", []),
                    CanonicalTypeId.Int16 => ("SMALLINT", []),
                    CanonicalTypeId.Int32 => ("INTEGER", []),
                    CanonicalTypeId.Int64 => ("BIGINT", []),
                    CanonicalTypeId.Decimal => DecimalForClamp(t, 38, "DuckDB"),
                    CanonicalTypeId.Single => ("FLOAT", []),
                    CanonicalTypeId.Double => ("DOUBLE", []),
                    CanonicalTypeId.Char => ($"VARCHAR({t.Length ?? 1})", W("DuckDB 不定长，CHAR 语义不强制")),
                    CanonicalTypeId.VarChar => t.Length is null
                        ? ("VARCHAR", [])
                        : ($"VARCHAR({t.Length})", []),
                    CanonicalTypeId.Text => ("VARCHAR", []),
                    CanonicalTypeId.Date => ("DATE", []),
                    CanonicalTypeId.Time => ("TIME", []),
                    CanonicalTypeId.DateTime => ("TIMESTAMP", []),
                    CanonicalTypeId.DateTimeOffset => ("TIMESTAMPTZ", []),
                    CanonicalTypeId.Binary => ("BLOB", []),
                    CanonicalTypeId.Guid => ("UUID", []),
                    CanonicalTypeId.Json => ("JSON", []),
                    CanonicalTypeId.Xml => ("VARCHAR", W("XML 以 VARCHAR 存储")),
                    _ => ("VARCHAR", W($"未识别的类型 {t.Id}，已降级为 VARCHAR")),
                };

            case ArkDialect.DM8:
                return t.Id switch
                {
                    CanonicalTypeId.Boolean => ("BIT", W("布尔类型映射为 BIT(0/1)")),
                    CanonicalTypeId.Int16 => ("SMALLINT", []),
                    CanonicalTypeId.Int32 => ("INTEGER", []),
                    CanonicalTypeId.Int64 => ("BIGINT", []),
                    CanonicalTypeId.Decimal => t.Precision is null
                        ? ("NUMBER", [])
                        : DecimalForClamp(t, 38, "DM8"),
                    CanonicalTypeId.Single => ("BINARY_FLOAT", []),
                    CanonicalTypeId.Double => ("BINARY_DOUBLE", []),
                    CanonicalTypeId.Char => ($"CHAR({t.Length ?? 1})", []),
                    CanonicalTypeId.VarChar => VarCharForDm(t),
                    CanonicalTypeId.Text => ("CLOB", []),
                    CanonicalTypeId.Date => ("DATE", W("DM8 的 DATE 含时间部分")),
                    CanonicalTypeId.Time => ("TIME", []),
                    CanonicalTypeId.DateTime => ("TIMESTAMP(6)", []),
                    CanonicalTypeId.DateTimeOffset => ("TIMESTAMP(6) WITH TIME ZONE", []),
                    CanonicalTypeId.Binary => ("BLOB", []),
                    CanonicalTypeId.Guid => ("VARCHAR(36)", W("UUID 以 VARCHAR(36) 文本存储")),
                    CanonicalTypeId.Json => ("CLOB", W("JSON 以 CLOB 文本存储")),
                    CanonicalTypeId.Xml => ("CLOB", W("XML 以 CLOB 文本存储")),
                    _ => ("CLOB", W($"未识别的类型 {t.Id}，已降级为 CLOB")),
                };

            case ArkDialect.SQLite:
            default:
                return t.Id switch
                {
                    CanonicalTypeId.Boolean => ("INTEGER", W("SQLite 动态类型：布尔按整数 0/1 存储")),
                    CanonicalTypeId.Int16 or CanonicalTypeId.Int32 or CanonicalTypeId.Int64 => ("INTEGER", []),
                    CanonicalTypeId.Decimal => ("NUMERIC", W("SQLite NUMERIC 亲和不强制十进制精度")),
                    CanonicalTypeId.Single or CanonicalTypeId.Double => ("REAL", []),
                    CanonicalTypeId.Char => ($"CHAR({t.Length ?? 1})", W("SQLite 不强制字符长度")),
                    CanonicalTypeId.VarChar => t.Length is null
                        ? ("TEXT", [])
                        : ($"VARCHAR({t.Length})", W("SQLite 不强制字符长度")),
                    CanonicalTypeId.Text => ("TEXT", []),
                    CanonicalTypeId.Date => ("DATE", []),
                    CanonicalTypeId.Time => ("TIME", []),
                    CanonicalTypeId.DateTime => ("DATETIME", []),
                    CanonicalTypeId.DateTimeOffset => ("DATETIME", W("SQLite 无时区感知类型，按 UTC 文本存储")),
                    CanonicalTypeId.Binary => ("BLOB", []),
                    CanonicalTypeId.Guid => ("TEXT", W("UUID 在 SQLite 中按文本存储")),
                    CanonicalTypeId.Json => ("TEXT", W("JSON 在 SQLite 中按文本存储，不校验格式")),
                    CanonicalTypeId.Xml => ("TEXT", []),
                    _ => ("TEXT", W($"未识别的类型 {t.Id}，已降级为 TEXT")),
                };
        }
    }

    private static (string, string[]) DecimalForMySql(CanonicalType t)
    {
        if (t.Precision is null) return ("decimal(18,6)", ["源类型未声明精度，目标使用 decimal(18,6)，请人工确认"]);
        var p = t.Precision.Value;
        var s = t.Scale ?? 0;
        var warns = new List<string>();
        if (p > 65) { warns.Add($"十进制精度 {p} 超出 MySQL 上限 65，已截断为 65"); p = 65; }
        if (s > 30) { warns.Add($"小数位 {s} 超出 MySQL 上限 30，已截断为 30"); s = 30; }
        if (s >= p) { s = p - 1; warns.Add("小数位不小于精度，已调整为 p-1"); }
        return ($"decimal({p},{s})", warns.ToArray());
    }

    private static (string, string[]) VarCharForMySql(CanonicalType t)
    {
        if (t.Length is null) return ("varchar(255)", ["变长字符串未声明长度，默认为 varchar(255)"]);
        var len = t.Length.Value;
        if (len > 16383)
            return ("text", new[] { $"varchar 长度 {len} 超出 utf8mb4 行限制，已降级为 text" });
        return ($"varchar({len})", []);
    }

    /// <summary>Decimal 通用输出：未声明精度用 decimal(18,6) 警告，按方言上限截断 P/S。</summary>
    private static (string Sql, string[] Warnings) DecimalForClamp(CanonicalType t, int maxP, string dialectName)
    {
        if (t.Precision is null) return ("decimal(18,6)", ["源类型未声明精度，目标使用 decimal(18,6)，请人工确认"]);
        var p = t.Precision.Value;
        var s = t.Scale ?? 0;
        var warns = new List<string>();
        if (p > maxP) { warns.Add($"十进制精度 {p} 超出 {dialectName} 上限 {maxP}，已截断为 {maxP}"); p = maxP; }
        if (s >= p) { s = p - 1; warns.Add("小数位不小于精度，已调整为 p-1"); }
        return ($"decimal({p},{s})", warns.ToArray());
    }

    private static (string, string[]) VarCharForSqlServer(CanonicalType t)
    {
        if (t.Length is null) return ("nvarchar(255)", ["变长字符串未声明长度，默认为 nvarchar(255)"]);
        var len = t.Length.Value;
        if (len > 4000)
            return ("nvarchar(max)", new[] { $"nvarchar 长度 {len} 超出 4000，已降级为 nvarchar(max)" });
        return ($"nvarchar({len})", []);
    }

    private static (string, string[]) VarCharForDm(CanonicalType t)
    {
        if (t.Length is null) return ("VARCHAR2(255)", ["变长字符串未声明长度，默认为 VARCHAR2(255)"]);
        var len = t.Length.Value;
        if (len > 8188)
            return ("CLOB", new[] { $"VARCHAR2 长度 {len} 超出页大小限制，已降级为 CLOB" });
        return ($"VARCHAR2({len})", []);
    }
}
