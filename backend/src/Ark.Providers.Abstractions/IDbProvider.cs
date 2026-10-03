using System.Data.Common;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Sync;

namespace Ark.Providers.Abstractions;

/// <summary>数据库提供者抽象：元数据、DDL 执行、行读写、SQL 执行。一次请求创建一个实例。</summary>
public interface IDbProvider : IAsyncDisposable
{
    ArkDialect Dialect { get; }
    DbCapabilities Capabilities { get; }
    IDdlGenerator Ddl { get; }

    /// <summary>测试连接，返回服务器版本字符串。</summary>
    Task<string> TestAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(string database, CancellationToken ct = default);
    Task<IReadOnlyList<TableSummary>> ListTablesAsync(string database, string? schema, CancellationToken ct = default);
    Task<CanonicalTable> GetTableAsync(string database, string? schema, string table, CancellationToken ct = default);
    Task<string?> GetCreateTableSqlAsync(string database, string? schema, string table, CancellationToken ct = default);
    Task<long> EstimateRowsAsync(TableRef table, CancellationToken ct = default);

    /// <summary>检查目标库是否具备所需内置函数（插件函数依赖检查）。</summary>
    Task<IReadOnlyList<ExtensionDependency>> CheckFunctionsAsync(
        string database, IReadOnlyList<string> functions, CancellationToken ct = default);

    /// <summary>视图定义原文（pg_get_viewdef / SHOW CREATE VIEW / sqlite_master.sql）。不存在返回 null。</summary>
    Task<string?> GetViewDefinitionAsync(string database, string? schema, string view, CancellationToken ct = default);

    /// <summary>打开源端一致快照会话（专用连接 + 事务）。同步引擎的全部源端读走该会话。</summary>
    Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default);

    /// <summary>打开目标端批量写入通道（COPY / LOAD DATA / 批量绑定；降级为多行 INSERT）。</summary>
    Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default);

    /// <summary>批量装载期间的会话调优语句（写连接专用，同步前执行；不支持返回空）。</summary>
    IReadOnlyList<string> BulkLoadPragmas => [];

    /// <summary>逐条执行 DDL/管理语句，返回成功条数与错误列表。</summary>
    Task<(int Executed, IReadOnlyList<string> Errors)> ExecuteDdlAsync(
        string database, IReadOnlyList<string> statements, CancellationToken ct = default);

    Task<QueryResponse> ExecuteQueryAsync(string database, string sql, int maxRows, int timeoutSeconds, CancellationToken ct = default);

    /// <summary>SQL 控制台补全元数据：当前 schema 的表/视图及其列（含类型名）。</summary>
    Task<CompletionSchema> GetCompletionSchemaAsync(string database, string? schema, CancellationToken ct = default);

    /// <summary>EXPLAIN 执行计划（统一文本；PG 为 pretty JSON 文本）。不执行语句本身。</summary>
    Task<ExplainResponse> ExplainAsync(string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default);

    Task<RowPage> ReadRowsAsync(TableRef table, CanonicalTable meta, RowQueryRequest query, CancellationToken ct = default);

    /// <summary>提交数据网格变更集（单事务）。</summary>
    Task<ApplyChangesResponse> ApplyChangesAsync(TableRef table, CanonicalTable meta, RowChangeSet changes, CancellationToken ct = default);

    /// <summary>为同步引擎打开一条独立连接（System.Data.Common 级别，供流式读写）。</summary>
    Task<DbConnection> OpenConnectionAsync(string? database, CancellationToken ct = default);
}
