using System.ComponentModel.DataAnnotations;
using Ark.Core.Errors;
using Ark.Core.Metadata;

namespace Ark.Core.Dtos;

// ------------------------- 连接 -------------------------

public sealed record ConnectionDto(
    Guid Id,
    string Name,
    ArkDialect Dialect,
    string? Host,
    int? Port,
    string? Username,
    bool HasPassword,
    string? Database,
    string? FilePath,
    bool ReadOnly,
    IReadOnlyDictionary<string, string> Options,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class SaveConnectionRequest
{
    [Required, MaxLength(100)] public string Name { get; init; } = "";
    [EnumDataType(typeof(ArkDialect))] public ArkDialect Dialect { get; init; }
    [MaxLength(255)] public string? Host { get; init; }
    [Range(1, 65535)] public int? Port { get; init; }
    [MaxLength(200)] public string? Username { get; init; }
    /// <summary>仅写入；GET 永不回显。</summary>
    [MaxLength(512)] public string? Password { get; init; }
    public string? Database { get; init; }
    public string? FilePath { get; init; }
    public bool ReadOnly { get; init; }
    public Dictionary<string, string>? Options { get; init; }

    public void ValidateForDialect()
    {
        if (Dialect == ArkDialect.SQLite)
        {
            if (string.IsNullOrWhiteSpace(FilePath))
                throw ArkException.Validation("SQLite 连接必须提供文件路径 FilePath");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw ArkException.Validation($"{Dialect} 连接必须提供 Host");
        }
    }
}

public sealed record TestConnectionResponse(bool Success, string? ServerVersion, double ElapsedMs, string? Error);

// ------------------------- 元数据 -------------------------

public sealed record TableDetailResponse(
    CanonicalTable Table,
    string? CreateSql);

// ------------------------- 表设计器 -------------------------

public sealed record CreateTableRequest
{
    [Required] public required CanonicalTable Table { get; init; }
    public bool Execute { get; init; } = true;
}

public sealed record AlterTableRequest
{
    [Required] public required CanonicalTable Table { get; init; }
    public bool Execute { get; init; } = true;
}

public sealed record DdlPreviewResponse(IReadOnlyList<string> Statements, IReadOnlyList<string> Warnings);

/// <summary>Warnings 与 preview 保持一致：类型截断、方言降级等警告在执行后同样可见。</summary>
public sealed record DdlExecuteResponse(
    IReadOnlyList<string> Statements, int Executed, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

// ------------------------- SQL 控制台 -------------------------

public sealed record ExecuteSqlRequest
{
    [Required] public string Database { get; init; } = "";
    public string? Schema { get; init; }
    [Required, MinLength(1)] public string Sql { get; init; } = "";
    [Range(1, 100_000)] public int MaxRows { get; init; } = 1000;
    [Range(1, 600)] public int TimeoutSeconds { get; init; } = 30;
}

public sealed record ExplainRequest
{
    [Required] public string Database { get; init; } = "";
    public string? Schema { get; init; }
    [Required, MinLength(1)] public string Sql { get; init; } = "";
    [Range(1, 600)] public int TimeoutSeconds { get; init; } = 30;
}

// ------------------------- AI 助手 -------------------------

public sealed record AiSettingsDto(bool Enabled, string BaseUrl, string Model, bool HasApiKey);

public sealed class SaveAiSettingsRequest
{
    public bool Enabled { get; init; }
    [MaxLength(500)] public string? BaseUrl { get; init; }
    [MaxLength(200)] public string? Model { get; init; }
    /// <summary>仅写入；null 保持不变，空字符串清除。</summary>
    [MaxLength(512)] public string? ApiKey { get; init; }
}

public sealed record AiSqlRequest
{
    public Guid ConnectionId { get; init; }
    [Required] public string Database { get; init; } = "";
    public string? Schema { get; init; }
    [Required, MinLength(1), MaxLength(4000)] public string Prompt { get; init; } = "";
}

public sealed record AiExplainRequest
{
    public Guid ConnectionId { get; init; }
    [Required] public string Database { get; init; } = "";
    public string? Schema { get; init; }
    [Required, MinLength(1), MaxLength(20_000)] public string Sql { get; init; } = "";
}

public sealed record AiFixRequest
{
    public Guid ConnectionId { get; init; }
    [Required] public string Database { get; init; } = "";
    public string? Schema { get; init; }
    [Required, MinLength(1), MaxLength(20_000)] public string Sql { get; init; } = "";
    [MaxLength(4000)] public string? Error { get; init; }
}

public sealed record AiTextResponse(string Text);

public sealed record AiTestResponse(bool Ok, string Message);

// ------------------------- 同步任务 -------------------------

public sealed record SyncTaskDto(Guid Id, string Status, string? CurrentTable, double Percent, string? Message,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error, string[] Log, object[] Reports);
