using System.Data.Common;

namespace Ark.Providers.Abstractions;

/// <summary>
/// 源端一致快照会话：在专用连接上打开事务（PG REPEATABLE READ / MySQL CONSISTENT SNAPSHOT / SQLite BEGIN）。
/// 同步引擎的全部源端读经由该会话，保证多表/多页读到同一版本。
/// </summary>
public interface ISnapshotSession : IAsyncDisposable
{
    DbConnection Connection { get; }

    /// <summary>快照隔离描述（用于任务日志，如 "REPEATABLE READ"）。</summary>
    string Description { get; }
}

/// <summary>
/// 原生批量写入通道：PG COPY (text format) / MySQL LOAD DATA LOCAL / SQLite 批量绑定。
/// 不可用时方言提供基于多行 INSERT 的降级实现。
/// </summary>
public interface IBulkWriter : IAsyncDisposable
{
    /// <summary>写入一行（值序与构造时的列序一致）。</summary>
    Task WriteAsync(object?[] values, CancellationToken ct = default);

    /// <summary>提交并关闭通道。</summary>
    Task CompleteAsync(CancellationToken ct = default);

    long RowsWritten { get; }

    /// <summary>通道描述（用于任务日志，如 "COPY" / "LOAD DATA" / "batched INSERT"）。</summary>
    string Channel { get; }
}
