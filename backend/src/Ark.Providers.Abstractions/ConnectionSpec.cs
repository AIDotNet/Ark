using Ark.Core.Metadata;

namespace Ark.Providers.Abstractions;

/// <summary>连接参数（与存储解耦；密码为明文，仅内存使用）。</summary>
public sealed record ConnectionSpec(
    ArkDialect Dialect,
    string? Host,
    int? Port,
    string? Username,
    string? Password,
    string? Database,
    string? FilePath,
    bool ReadOnly,
    IReadOnlyDictionary<string, string> Options);

/// <summary>方言能力矩阵，驱动前端禁用与同步计划降级。</summary>
public sealed record DbCapabilities(
    bool SupportsSchemas,
    bool SupportsMultipleDatabases,
    bool SupportsTruncate,
    bool SupportsDropColumn,
    bool SupportsAlterColumnType,
    bool SupportsIndexConcurrently,
    bool SupportsBulkCopy = false,
    bool SupportsConsistentSnapshot = false,
    string ServerKind = "");
