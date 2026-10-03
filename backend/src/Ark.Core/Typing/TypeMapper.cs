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

    // ------------------------------------------------------------------
    // ② Canonical → 目标方言 SQL
    // ------------------------------------------------------------------

    /// <summary>PostgreSQL 参数 cast 后缀（jsonb/timestamp 等不接受未知文本参数）；非生成列返回 null 表示无需。</summary>
    public static string? PgCastFor(CanonicalColumn col)
    {
        if (col.IsGenerated) return null;
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
            case ArkDialect.PostgreSQL:
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

            case ArkDialect.MySQL:
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
}
