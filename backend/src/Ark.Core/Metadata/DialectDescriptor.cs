using Ark.Core.Typing;

namespace Ark.Core.Metadata;

/// <summary>二进制字面量的方言格式（SQL dump 导出用）。</summary>
public enum BinaryLiteralFormat
{
    /// <summary>X'ABCD'（MySQL / SQLite / DuckDB）</summary>
    HexX,
    /// <summary>decode('abcd','hex')（PostgreSQL bytea）</summary>
    PgDecode,
    /// <summary>0xABCD（SQL Server）</summary>
    SqlServerHex,
    /// <summary>unhex('abcd')（ClickHouse）</summary>
    Unhex,
}

/// <summary>
/// 方言静态特征表：把原先散落在 Ark.Sync / Ark.Api / 前端的方言 switch 收敛到一处，
/// 新增数据库 = 在 <see cref="Dialects"/> 注册表里填一份条目 + 实现对应 Provider。
/// 仅承载“声明式”特征；真正的元数据查询/DDL 生成仍由各 Provider 实现。
/// </summary>
public sealed class DialectDescriptor
{
    public required ArkDialect Dialect { get; init; }

    // ---------------- 连接表单（后端校验与前端注册表共用语义） ----------------

    /// <summary>文件型连接（SQLite / DuckDB）：必须提供 FilePath，无 Host。</summary>
    public bool FileBased { get; init; }

    /// <summary>建连默认端口（文件型为 null）。</summary>
    public int? DefaultPort { get; init; }

    // ---------------- Schema 模型 ----------------

    /// <summary>存在独立 schema 层（PG / SQL Server / DM 等）。</summary>
    public bool SupportsSchemas { get; init; }

    /// <summary>目标 schema 的默认值（同步未指定时使用）。</summary>
    public string? DefaultTargetSchema { get; init; }

    /// <summary>支持 CREATE SCHEMA IF NOT EXISTS（仅 PG 系）。</summary>
    public bool SupportsCreateSchemaIfNotExists { get; init; }

    /// <summary>支持单语句 TRUNCATE 多表（PG 系，用于互引外键表的一次性预清理）。</summary>
    public bool SupportsMultiTableTruncate { get; init; }

    /// <summary>依赖 search_path 解析 DDL 中的裸表名（仅 PG 系），同步前后需临时切换。</summary>
    public bool UsesSearchPath { get; init; }

    /// <summary>对象限定名带库名（MySQL 系 `db`.`t` / ClickHouse "db"."t"）。</summary>
    public bool QualifiesWithDatabase { get; init; }

    // ---------------- 标识符引用 ----------------

    public char QuoteOpen { get; init; } = '"';
    public char QuoteClose { get; init; } = '"';

    // ---------------- 批量写 ----------------

    /// <summary>多值 INSERT 的参数总量上限（各方言驱动限制，预留余量前）。</summary>
    public int MaxBatchParams { get; init; } = 65000;

    /// <summary>SkipExisting 冲突子句；空串 = 目标方言不支持（调用方按普通 INSERT 处理并在计划中警告）。</summary>
    public string SkipExistingClause { get; init; } = " ON CONFLICT DO NOTHING";

    /// <summary>参数按目标列类型显式 cast（PG 系 ::type）；null = 不需要。</summary>
    public Func<CanonicalColumn, string?>? ParamCastFor { get; init; }

    /// <summary>驱动按位置绑定参数（DM 的 ADO 驱动用 ? 占位，不支持 @name 标记）。</summary>
    public bool PositionalParameters { get; init; }

    /// <summary>
    /// 参数名的驱动侧前缀。多数 ADO 驱动参数名为 "@f0"；
    /// ClickHouse.Client 按"@ + ParameterName"匹配 SQL 占位符，参数名必须不带 @（前缀空串）。
    /// </summary>
    public string ParamNamePrefix { get; init; } = "@";

    /// <summary>SQL 文本中的参数占位标记：位置绑定方言返回 ?；其余 前缀 + 裸名（"@f0" / "$p0"）。</summary>
    public string SqlMarker(string bareName) => PositionalParameters ? "?" : ParamMarkerPrefix + bareName;

    /// <summary>SQL 文本中命名参数的标记前缀：多数驱动 "@"（@f0）；DuckDB.NET 为 "$"（$p0）。</summary>
    public string ParamMarkerPrefix { get; init; } = "@";

    /// <summary>
    /// 构造驱动侧参数名：ClickHouse.Client 按"@ + ParameterName"匹配 → 裸名（f0）；
    /// DuckDB.NET 按 $name 匹配 → 裸名（p0）；其余为 "@f0"。
    /// </summary>
    public string MakeParamName(string bareName) => ParamNamePrefix + bareName;

    /// <summary>支持显式事务（BeginTransaction）。ClickHouse 无事务语义 → false（引擎按无事务执行）。</summary>
    public bool SupportsTransactions { get; init; } = true;

    /// <summary>行级 UPDATE/DELETE 走 ALTER TABLE ... UPDATE/DELETE mutation（ClickHouse），语句为异步生效。</summary>
    public bool UsesAlterTableMutations { get; init; }

    // ---------------- SQL 片段 ----------------

    /// <summary>分页子句（有 ORDER BY 的 SELECT 末尾）。SQL Server 用 OFFSET...FETCH。</summary>
    public string PageClause(int limit, long offset) => SupportsFetchFirst
        ? $" OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY"
        : $" LIMIT {limit} OFFSET {offset}";

    /// <summary>是否要求 OFFSET...FETCH 语法（不支持 LIMIT/OFFSET 的方言）。</summary>
    public bool SupportsFetchFirst { get; init; }

    /// <summary>对整段 SELECT 追加行数上限（SQL dump 导出用）。SQL Server 注入 TOP n。</summary>
    public string ApplyRowLimit(string sql, int limit) => SupportsFetchFirst
        ? InjectTop(sql, limit)
        : sql + $" LIMIT {limit}";

    private static string InjectTop(string sql, int limit)
    {
        // 仅处理本工具自产 SQL：以 SELECT [DISTINCT] 开头
        var idx = sql.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return sql + $" -- 限制行数 {limit}（非 SELECT 语句，未注入 TOP）";
        var afterSelect = idx + "SELECT".Length;
        var rest = sql[afterSelect..].TrimStart();
        var insertAt = afterSelect + (sql.Length - afterSelect - rest.Length); // SELECT 后的空白之后
        if (rest.StartsWith("DISTINCT", StringComparison.OrdinalIgnoreCase))
        {
            insertAt = insertAt + "DISTINCT".Length;
        }
        return sql[..insertAt] + $" TOP {limit}" + sql[insertAt..];
    }

    // ---------------- 会话语句 ----------------

    /// <summary>批量装载前的会话调优（写连接专用；Provider 可再覆写）。</summary>
    public IReadOnlyList<string> BulkLoadPragmas { get; init; } = [];

    /// <summary>批量装载后的恢复语句。</summary>
    public IReadOnlyList<string> ResetPragmas { get; init; } = [];

    /// <summary>清空目标表前的会话级 FK 开关（关闭）。空 = 无会话开关（靠先子后父顺序）。</summary>
    public IReadOnlyList<string> FkOffStatements { get; init; } = [];

    /// <summary>清空完成后的 FK 开关（恢复）。</summary>
    public IReadOnlyList<string> FkOnStatements { get; init; } = [];

    // ---------------- SQL 导出字面量 ----------------

    /// <summary>字符串字面量中反斜杠是转义字符（需翻倍）：MySQL 系 / ClickHouse。</summary>
    public bool BackslashEscapes { get; init; }

    /// <summary>布尔字面量写 1/0（SQLite / SQL Server / DM）；否则 TRUE/FALSE。</summary>
    public bool BoolAsOneZero { get; init; }

    public BinaryLiteralFormat BinaryFormat { get; init; } = BinaryLiteralFormat.HexX;

    // ---------------- 方法 ----------------

    public string QuoteIdentifier(string id) =>
        QuoteOpen + id.Replace(QuoteClose.ToString(), $"{QuoteClose}{QuoteClose}") + QuoteClose;

    /// <summary>视图/DDL 的对象限定名（对齐原 SyncPlanner.BuildCreateView 的三方言语义）。</summary>
    public string QuoteQualified(string? database, string? schema, string table)
    {
        if (QualifiesWithDatabase && !string.IsNullOrEmpty(database))
            return $"{QuoteIdentifier(database)}.{QuoteIdentifier(table)}";
        if (SupportsSchemas && !string.IsNullOrEmpty(schema))
            return $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";
        return QuoteIdentifier(table);
    }

    /// <summary>
    /// 同步目标端 schema 解析：仅当源/目标同为 PG 系（存在真实 schema 层）时继承源 schema，
    /// 否则回落到方言默认（跨族继承无意义：PG 的 public 对 SQL Server 应为 dbo，对 DM 为当前用户模式）。
    /// </summary>
    public string? ResolveTargetSchema(ArkDialect sourceDialect, string? sourceSchema)
    {
        if (!SupportsSchemas) return null;
        var sameFamily = PgLikeDialects.Contains(sourceDialect) && PgLikeDialects.Contains(Dialect);
        var inherited = sameFamily ? sourceSchema : null;
        return string.IsNullOrEmpty(inherited) ? DefaultTargetSchema : inherited;
    }

    public string? SetSearchPathSql(string schema) =>
        UsesSearchPath ? $"SET search_path TO {QuoteIdentifier(schema)}" : null;

    public string? ResetSearchPathSql() => UsesSearchPath ? "RESET search_path" : null;

    /// <summary>PG 系方言（存在真实 schema 层且类型/函数语法同源）。</summary>
    public static readonly IReadOnlySet<ArkDialect> PgLikeDialects =
        new HashSet<ArkDialect> { ArkDialect.PostgreSQL, ArkDialect.KingbaseES, ArkDialect.OpenGauss };

    /// <summary>MySQL 系方言（MySqlConnector 驱动同源）。</summary>
    public static readonly IReadOnlySet<ArkDialect> MySqlLikeDialects =
        new HashSet<ArkDialect> { ArkDialect.MySQL, ArkDialect.MariaDB };
}

/// <summary>方言特征注册表：按 <see cref="ArkDialect"/> 静态索引，无需实例化 Provider。</summary>
public static class Dialects
{
    public static DialectDescriptor Get(ArkDialect dialect) => Map.TryGetValue(dialect, out var d)
        ? d
        : throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "未知方言");

    private static readonly Dictionary<ArkDialect, DialectDescriptor> Map = new()
    {
        [ArkDialect.PostgreSQL] = new DialectDescriptor
        {
            Dialect = ArkDialect.PostgreSQL,
            DefaultPort = 5432,
            SupportsSchemas = true,
            DefaultTargetSchema = "public",
            SupportsCreateSchemaIfNotExists = true,
            SupportsMultiTableTruncate = true,
            UsesSearchPath = true,
            MaxBatchParams = 65000,
            SkipExistingClause = " ON CONFLICT DO NOTHING",
            ParamCastFor = Ark.Core.Typing.TypeMapper.PgCastFor,
            BulkLoadPragmas = ["SET synchronous_commit = off"],
            ResetPragmas = ["SET synchronous_commit=on"],
        },
        [ArkDialect.MySQL] = new DialectDescriptor
        {
            Dialect = ArkDialect.MySQL,
            DefaultPort = 3306,
            QualifiesWithDatabase = true,
            QuoteOpen = '`',
            QuoteClose = '`',
            MaxBatchParams = 65000,
            SkipExistingClause = " IGNORE",
            ResetPragmas =
            [
                // autocommit=1 提交遗留事务并释放 MDL：否则 AlignAutoIncrement 的 SELECT MAX 事务
                // 会与缓存连接上的 ALTER TABLE AUTO_INCREMENT 互相等 MDL，直到 60s 命令超时
                "SET autocommit=1",
                "SET FOREIGN_KEY_CHECKS=1",
                "SET unique_checks=1",
            ],
            FkOffStatements = ["SET FOREIGN_KEY_CHECKS=0"],
            FkOnStatements = ["SET FOREIGN_KEY_CHECKS=1"],
            BackslashEscapes = true,
        },
        [ArkDialect.SQLite] = new DialectDescriptor
        {
            Dialect = ArkDialect.SQLite,
            FileBased = true,
            MaxBatchParams = 900, // Microsoft.Data.Sqlite 兼容旧上限
            SkipExistingClause = " ON CONFLICT DO NOTHING",
            ResetPragmas = ["PRAGMA foreign_keys=ON"],
            FkOffStatements = ["PRAGMA foreign_keys=OFF"],
            FkOnStatements = ["PRAGMA foreign_keys=ON"],
            BoolAsOneZero = true,
        },
        [ArkDialect.MariaDB] = new DialectDescriptor
        {
            Dialect = ArkDialect.MariaDB,
            DefaultPort = 3306,
            QualifiesWithDatabase = true,
            QuoteOpen = '`',
            QuoteClose = '`',
            MaxBatchParams = 65000,
            SkipExistingClause = " IGNORE",
            ResetPragmas = ["SET autocommit=1", "SET FOREIGN_KEY_CHECKS=1", "SET unique_checks=1"],
            FkOffStatements = ["SET FOREIGN_KEY_CHECKS=0"],
            FkOnStatements = ["SET FOREIGN_KEY_CHECKS=1"],
            BackslashEscapes = true,
        },
        [ArkDialect.SqlServer] = new DialectDescriptor
        {
            Dialect = ArkDialect.SqlServer,
            DefaultPort = 1433,
            SupportsSchemas = true,
            DefaultTargetSchema = "dbo",
            QuoteOpen = '[',
            QuoteClose = ']',
            MaxBatchParams = 2000, // SqlClient 上限 2100，留余量
            SkipExistingClause = "", // 无 ON CONFLICT；SkipExisting 在该目标暂不支持（计划中警告）
            SupportsFetchFirst = true, // 不支持 LIMIT/OFFSET，用 OFFSET...FETCH
            BoolAsOneZero = true,
            BinaryFormat = BinaryLiteralFormat.SqlServerHex,
        },
        [ArkDialect.ClickHouse] = new DialectDescriptor
        {
            Dialect = ArkDialect.ClickHouse,
            DefaultPort = 8123,
            QualifiesWithDatabase = true,
            MaxBatchParams = 65000,
            SkipExistingClause = "", // 无 ON CONFLICT；去重依赖引擎（ReplacingMergeTree）
            ParamNamePrefix = "", // ClickHouse.Client 按 "@" + ParameterName 匹配，参数名不带 @
            SupportsTransactions = false, // 无事务：引擎按无事务执行（失败不回滚）
            UsesAlterTableMutations = true, // 行级 UPDATE/DELETE 走 ALTER TABLE ... UPDATE/DELETE（mutation，异步生效）
            BackslashEscapes = true,
            BinaryFormat = BinaryLiteralFormat.Unhex,
        },
        [ArkDialect.DuckDB] = new DialectDescriptor
        {
            Dialect = ArkDialect.DuckDB,
            FileBased = true,
            MaxBatchParams = 65000,
            SkipExistingClause = " ON CONFLICT DO NOTHING",
            ParamNamePrefix = "", // DuckDB.NET 按 $name 匹配命名参数（@name 会被解析为标识符）
            ParamMarkerPrefix = "$",
            BinaryFormat = BinaryLiteralFormat.HexX,
        },
        [ArkDialect.DM8] = new DialectDescriptor
        {
            Dialect = ArkDialect.DM8,
            DefaultPort = 5236,
            SupportsSchemas = true,
            DefaultTargetSchema = null, // ≈ 当前用户模式，不显式默认
            QuoteOpen = '"',
            QuoteClose = '"',
            MaxBatchParams = 65000,
            SkipExistingClause = "", // 仅 MERGE；v1 暂不支持 SkipExisting
            PositionalParameters = true, // DmCommand 按 ? 位置绑定
            BoolAsOneZero = true,
        },
        [ArkDialect.KingbaseES] = new DialectDescriptor
        {
            Dialect = ArkDialect.KingbaseES,
            DefaultPort = 54321,
            SupportsSchemas = true,
            DefaultTargetSchema = "public",
            SupportsCreateSchemaIfNotExists = true,
            SupportsMultiTableTruncate = true,
            UsesSearchPath = true,
            MaxBatchParams = 65000,
            SkipExistingClause = " ON CONFLICT DO NOTHING",
            ParamCastFor = Ark.Core.Typing.TypeMapper.PgCastFor,
            BulkLoadPragmas = ["SET synchronous_commit = off"],
            ResetPragmas = ["SET synchronous_commit=on"],
        },
        [ArkDialect.OpenGauss] = new DialectDescriptor
        {
            Dialect = ArkDialect.OpenGauss,
            DefaultPort = 5432,
            SupportsSchemas = true,
            DefaultTargetSchema = "public",
            SupportsCreateSchemaIfNotExists = true,
            SupportsMultiTableTruncate = true,
            UsesSearchPath = true,
            MaxBatchParams = 65000,
            SkipExistingClause = " ON CONFLICT DO NOTHING",
            ParamCastFor = Ark.Core.Typing.TypeMapper.PgCastFor,
        },
    };
}
