namespace Ark.Core.Responses;

/// <summary>统一响应包络。所有 /api 端点（除标记跳过的原始流）都返回该结构。</summary>
public sealed record ApiEnvelope<T>(int Code, string Message, T? Data, string TraceId, DateTimeOffset Timestamp)
{
    public static ApiEnvelope<T> Ok(T? data, string traceId) =>
        new(ArkErrorCodes.Success, "ok", data, traceId, DateTimeOffset.UtcNow);

    public static ApiEnvelope<T> Error(int code, string message, string traceId) =>
        new(code, message, default, traceId, DateTimeOffset.UtcNow);
}

/// <summary>错误码分段：0 成功；1xxx 通用；2xxx 连接；3xxx 元数据/DDL；4xxx 数据；5xxx 同步；6xxx AI。</summary>
public static class ArkErrorCodes
{
    public const int Success = 0;

    // 1xxx 通用
    public const int Unknown = 1000;
    public const int Validation = 1400;
    public const int NotFound = 1404;

    // 2xxx 连接
    public const int ConnectionFailed = 2001;
    public const int ConnectionNotFound = 2002;
    public const int ConnectionConfigInvalid = 2003;
    public const int ReadOnlyMode = 2004;

    // 3xxx 元数据 / DDL
    public const int MetadataQueryFailed = 3001;
    public const int TableNotFound = 3002;
    public const int DdlFailed = 3003;
    public const int IdentifierInvalid = 3004;

    // 4xxx 数据
    public const int RowQueryFailed = 4001;
    public const int RowChangeFailed = 4002;
    public const int FilterInvalid = 4003;

    // 5xxx 同步
    public const int SyncPlanFailed = 5001;
    public const int SyncExecuteFailed = 5002;
    public const int SyncTaskNotFound = 5003;

    // 6xxx AI
    public const int AiNotConfigured = 6001;
    public const int AiFailed = 6002;
}
