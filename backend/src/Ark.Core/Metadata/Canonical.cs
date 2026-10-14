using System.Text.RegularExpressions;
using Ark.Core.Errors;
using Ark.Core.Responses;

namespace Ark.Core.Metadata;

public enum ArkDialect
{
    // 注意：值按 ark.db 持久化（long），新方言只能追加在末尾，不得插入/重排。
    PostgreSQL = 0,
    MySQL = 1,
    SQLite = 2,
    MariaDB = 3,
    SqlServer = 4,
    ClickHouse = 5,
    DuckDB = 6,
    DM8 = 7,
    KingbaseES = 8,
    OpenGauss = 9,
}

public enum CanonicalTypeId
{
    Boolean,
    Int16,
    Int32,
    Int64,
    Decimal,
    Single,
    Double,
    Char,
    VarChar,
    Text,
    Date,
    Time,
    DateTime,
    DateTimeOffset,
    Binary,
    Guid,
    Json,
    Xml,
    Unknown,
}

/// <summary>跨库中间类型表示。</summary>
public sealed record CanonicalType(CanonicalTypeId Id, int? Length = null, int? Precision = null, int? Scale = null)
{
    public static CanonicalType Of(CanonicalTypeId id) => new(id);

    public static readonly CanonicalType Bool = Of(CanonicalTypeId.Boolean);
    public static readonly CanonicalType Int = Of(CanonicalTypeId.Int32);
    public static readonly CanonicalType Long = Of(CanonicalTypeId.Int64);
    public static readonly CanonicalType Str = Of(CanonicalTypeId.Text);
    public static readonly CanonicalType Blob = Of(CanonicalTypeId.Binary);

    public override string ToString()
    {
        var baseName = Id.ToString();
        return Id switch
        {
            CanonicalTypeId.Char or CanonicalTypeId.VarChar when Length is not null => $"{baseName}({Length})",
            CanonicalTypeId.Decimal when Precision is not null => $"{baseName}({Precision},{Scale ?? 0})",
            _ => baseName,
        };
    }
}

/// <summary>默认值/生成列表达式的语义类别（用于跨库函数翻译）。</summary>
public enum ValueExprKind
{
    None,
    Literal,          // 字符串/数字字面量，原样复制
    BooleanLiteral,   // true/false：SQL Server/SQLite/DM8 发射 1/0，其余方言发射 true/false
    TypedLiteral,     // 'v'::type 显式转换字面量：剥离转换，仅保留字面量本体
    ArrayLiteral,     // 数组字面量（'{}'::text[] / ARRAY[..] / [..]）：发射为 JSON 数组（数组列已降级为 Json/文本）
    CurrentTimestamp,
    CurrentDate,
    CurrentTime,
    UuidV4,
    Coalesce,
    Concat,
    Unrecognized,     // 无法识别的表达式：原样复制并给出警告
}

public sealed record CanonicalColumn
{
    public required string Name { get; init; }
    public required CanonicalType Type { get; init; }
    public bool Nullable { get; init; } = true;
    public string? DefaultValueSql { get; init; }
    public ValueExprKind DefaultKind { get; init; }
    public bool IsAutoIncrement { get; init; }
    public bool IsPrimaryKey { get; init; }
    public string? Comment { get; init; }
    public bool IsGenerated { get; init; }
    public string? GeneratedExpression { get; init; }
    /// <summary>
    /// 方言原始类型名（如 "smallint[]"、"numeric(10,2)"，来自 format_type 等）。
    /// PG 系写入端参数 cast 优先使用它：数组等类型在规范模型中降级（smallint[]→Json），
    /// 按规范类型反推会得到错误的 ::jsonb（42804）。
    /// </summary>
    public string? NativeType { get; init; }
}

public sealed record CanonicalIndex
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public string? Method { get; init; }
    public string? WhereClause { get; init; }
}

public sealed record CanonicalForeignKey
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required string ReferencedTable { get; init; }
    public required IReadOnlyList<string> ReferencedColumns { get; init; }
    public string? OnDelete { get; init; }
    public string? OnUpdate { get; init; }
}

/// <summary>统一四层模型：Connection → Database → Schema → Table。MySQL schema==database；SQLite 固定 main。</summary>
public sealed record TableRef(string Database, string? Schema, string Table);

public sealed record DatabaseInfo(string Name);
public sealed record SchemaInfo(string Name);

public sealed record TableSummary(string Name, string Kind, long? EstimatedRows);

/// <summary>表的中间规范表示，是结构同步与设计器的统一载体。</summary>
public sealed record CanonicalTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<CanonicalColumn> Columns { get; init; }
    public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = [];
    public IReadOnlyList<CanonicalIndex> Indexes { get; init; } = [];
    public IReadOnlyList<CanonicalForeignKey> ForeignKeys { get; init; } = [];
    public string? Comment { get; init; }
    public IReadOnlyDictionary<string, string?> Options { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    public bool IsView { get; init; }
}

/// <summary>标识符合法性校验（防止 SQL 注入到 DDL/标识符位置）。</summary>
public static partial class Identifier
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$#]*$", RegexOptions.Compiled)]
    private static partial Regex Pattern();

    public static bool IsValid(string? name) => !string.IsNullOrEmpty(name) && Pattern().IsMatch(name);

    public static void EnsureValid(string? name, string kind = "标识符")
    {
        if (!IsValid(name))
            throw new ArkException(ArkErrorCodes.IdentifierInvalid, $"{kind}不合法: \"{name}\"（仅允许字母、数字、下划线，且以字母或下划线开头）");
    }
}
