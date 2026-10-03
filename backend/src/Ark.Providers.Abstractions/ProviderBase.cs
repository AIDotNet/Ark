using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;

namespace Ark.Providers.Abstractions;

/// <summary>三库共享的实现：连接缓存、行查询（结构化筛选/排序/分页）、变更集提交、SQL 执行、DDL 执行。</summary>
public abstract class ProviderBase : IDbProvider
{
    private readonly Dictionary<string, DbConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    protected ProviderBase(ConnectionSpec spec) => Spec = spec;

    protected ConnectionSpec Spec { get; }

    public abstract ArkDialect Dialect { get; }
    public abstract DbCapabilities Capabilities { get; }
    public abstract IDdlGenerator Ddl { get; }
    protected abstract DbConnection CreateConnection(string? database);

    /// <summary>方言内置且可用的函数集合（子类可覆写以做真实检查）。</summary>
    protected virtual IReadOnlySet<string> BuiltinFunctions => new HashSet<string>();

    // ------------------------------------------------ 连接 ------------------------------------------------

    protected async Task<DbConnection> GetConnAsync(string? database, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = database ?? "";
        if (_connections.TryGetValue(key, out var existing) && existing.State == ConnectionState.Open)
            return existing;
        var conn = CreateConnection(database);
        await conn.OpenAsync(ct);
        _connections[key] = conn;
        return conn;
    }

    public async Task<DbConnection> OpenConnectionAsync(string? database, CancellationToken ct = default)
    {
        var conn = CreateConnection(database);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ------------------------------------------------ DDL ------------------------------------------------

    public virtual async Task<(int Executed, IReadOnlyList<string> Errors)> ExecuteDdlAsync(
        string database, IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        if (Spec.ReadOnly)
            throw new ArkException(ArkErrorCodes.ReadOnlyMode, "连接处于只读模式，无法执行 DDL", 403);
        var conn = await GetConnAsync(database, ct);
        var errors = new List<string>();
        var executed = 0;
        foreach (var s in statements)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = s;
                await cmd.ExecuteNonQueryAsync(ct);
                executed++;
            }
            catch (Exception ex)
            {
                errors.Add($"[{Compact(s)}] {ex.Message}");
            }
        }
        return (executed, errors);
    }

    // ------------------------------------------------ SQL 查询 ------------------------------------------------

    public virtual async Task<QueryResponse> ExecuteQueryAsync(
        string database, string sql, int maxRows, int timeoutSeconds, CancellationToken ct = default)
    {
        ReadOnlyGuard(sql);
        var sw = Stopwatch.StartNew();
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = timeoutSeconds;

        var results = new List<QueryResultSet>();
        var messages = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var index = 0;
        do
        {
            index++;
            var cols = new List<QueryColumn>();
            for (var i = 0; i < reader.FieldCount; i++)
                cols.Add(new QueryColumn(reader.GetName(i), reader.GetDataTypeName(i)));
            var rows = new List<object?[]>();
            while (rows.Count < maxRows && await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                reader.GetValues(row);
                CellValue.NormalizeRow(row);
                rows.Add(row);
            }
            if (rows.Count >= maxRows)
                messages.Add($"结果集 {index} 已达到上限 {maxRows} 行，已截断");
            results.Add(new QueryResultSet(cols, rows, reader.RecordsAffected));
        } while (await reader.NextResultAsync(ct));

        return new QueryResponse(results, messages, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
    }

    // ------------------------------------------------ 补全元数据 / 执行计划 ------------------------------------------------

    /// <summary>默认实现：逐表 GetTableAsync（建议各方言覆写为批量查询）。</summary>
    public virtual async Task<CompletionSchema> GetCompletionSchemaAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var tables = await ListTablesAsync(database, schema, ct);
        var list = new List<CompletionTable>(tables.Count);
        foreach (var t in tables)
        {
            try
            {
                var meta = await GetTableAsync(database, schema, t.Name, ct);
                list.Add(new CompletionTable(t.Name, t.Kind,
                    meta.Columns.Select(c => new CompletionColumn(c.Name, c.Type.Id.ToString())).ToList()));
            }
            catch
            {
                // 单表元数据读取失败不阻塞整体补全
            }
        }
        return new CompletionSchema(list);
    }

    public abstract Task<ExplainResponse> ExplainAsync(
        string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default);

    /// <summary>读取 EXPLAIN 结果为文本行；只读连接先校验被解释语句为查询类。</summary>
    protected async Task<ExplainResponse> ReadPlanAsync(
        string database, string explainSql, string sql, int timeoutSeconds, CancellationToken ct,
        Func<DbDataReader, string>? formatRow = null)
    {
        EnsureSelectable(sql);
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = explainSql;
        cmd.CommandTimeout = timeoutSeconds;
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (formatRow is not null)
            {
                lines.Add(formatRow(reader));
                continue;
            }
            var parts = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                parts[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "NULL";
            lines.Add(string.Join(" | ", parts));
        }
        return new ExplainResponse(string.Join("\n", lines));
    }

    /// <summary>只读连接下，仅允许对查询类语句生成执行计划（EXPLAIN 本身不执行，但 EXPLAIN ANALYZE 会）。</summary>
    protected void EnsureSelectable(string sql)
    {
        if (!Spec.ReadOnly) return;
        var first = sql.AsSpan().TrimStart();
        foreach (var sep in new[] { ' ', '\n', '\r', '\t', '(', ';' })
        {
            var idx = first.IndexOf(sep);
            if (idx > 0) first = first[..idx];
        }
        var kw = first.ToString().ToUpperInvariant();
        if (kw is not ("SELECT" or "WITH" or "TABLE" or "VALUES" or "SHOW" or "PRAGMA"))
            throw new ArkException(ArkErrorCodes.ReadOnlyMode, "只读连接仅允许对查询语句生成执行计划", 403);
    }

    protected void ReadOnlyGuard(string sql)
    {
        if (!Spec.ReadOnly) return;
        var first = sql.AsSpan().TrimStart();
        foreach (var sep in new[] { ' ', '\n', '\r', '\t', '(', ';' })
        {
            var idx = first.IndexOf(sep);
            if (idx > 0) first = first[..idx];
        }
        var kw = first.ToString().ToUpperInvariant();
        if (kw is not ("SELECT" or "WITH" or "EXPLAIN" or "SHOW" or "PRAGMA" or "VALUES" or "TABLE"))
            throw new ArkException(ArkErrorCodes.ReadOnlyMode, "连接处于只读模式，已拒绝执行写操作", 403);
    }

    // ------------------------------------------------ 行读取 ------------------------------------------------

    public virtual async Task<RowPage> ReadRowsAsync(
        TableRef table, CanonicalTable meta, RowQueryRequest query, CancellationToken ct = default)
    {
        foreach (var f in query.Filters) Identifier.EnsureValid(f.Column, "筛选列名");
        foreach (var o in query.OrderBy) Identifier.EnsureValid(o.Column, "排序列名");

        // 过滤/排序列必须是表内真实列：SQLite DQS 下未知列会按字面量比较，静默返回空集/错序
        var knownCols = meta.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in query.Filters)
            if (!knownCols.Contains(f.Column))
                throw ArkException.Validation($"列 {f.Column} 不存在");
        foreach (var o in query.OrderBy)
            if (!knownCols.Contains(o.Column))
                throw ArkException.Validation($"列 {o.Column} 不存在");

        var conn = await GetConnAsync(table.Database, ct);
        await using var cmd = conn.CreateCommand();
        var where = BuildWhere(query.Filters, cmd, meta);
        var order = BuildOrder(query.OrderBy, meta);

        cmd.CommandText = $"SELECT COUNT(*) FROM {Ddl.TableName(table)}{where}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        cmd.CommandText = $"SELECT * FROM {Ddl.TableName(table)}{where}{order} LIMIT {query.Limit} OFFSET {query.Offset}";
        var cols = new List<QueryColumn>();
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        for (var i = 0; i < reader.FieldCount; i++)
            cols.Add(new QueryColumn(reader.GetName(i), reader.GetDataTypeName(i)));
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            CellValue.NormalizeRow(row);
            rows.Add(row);
        }
        return new RowPage(cols, rows, total, query.Limit, query.Offset, meta.PrimaryKeyColumns);
    }

    protected string BuildWhere(IReadOnlyList<FilterClause> filters, DbCommand cmd, CanonicalTable meta)
    {
        if (filters.Count == 0) return "";
        var parts = new List<string>();
        var i = 0;
        foreach (var f in filters)
        {
            Identifier.EnsureValid(f.Column, "筛选列名");
            var col = Ddl.Quote(f.Column);
            var type = meta.Columns.FirstOrDefault(c => string.Equals(c.Name, f.Column, StringComparison.OrdinalIgnoreCase))?.Type
                       ?? CanonicalType.Str;

            switch (f.Op)
            {
                case "is_null":
                    parts.Add($"{col} IS NULL");
                    break;
                case "is_not_null":
                    parts.Add($"{col} IS NOT NULL");
                    break;
                case "in" or "not_in":
                {
                    var ps = new List<string>();
                    foreach (var v in f.Values ?? [])
                    {
                        var p = cmd.CreateParameter();
                        p.ParameterName = $"@f{i++}";
                        p.Value = CellValue.ToParam(CellValue.FromJson(v, type), Dialect);
                        cmd.Parameters.Add(p);
                        ps.Add(p.ParameterName);
                    }
                    parts.Add(ps.Count == 0
                        ? "1=0"
                        : f.Op == "in"
                            ? $"{col} IN ({string.Join(", ", ps)})"
                            : $"({col} NOT IN ({string.Join(", ", ps)}) OR {col} IS NULL)");
                    break;
                }
                default:
                {
                    if (f.Value is not { } raw) throw ArkException.Validation($"筛选 {f.Column} {f.Op} 缺少比较值");
                    var op = f.Op switch
                    {
                        "=" => "=",
                        "!=" or "<>" => "<>",
                        ">" => ">", "<" => "<", ">=" => ">=", "<=" => "<=",
                        "like" => "LIKE",
                        "not_like" => "NOT LIKE",
                        _ => throw ArkException.Validation($"不支持的筛选操作符: {f.Op}"),
                    };
                    var val = CellValue.FromJson(raw, type);
                    if (f.Op is "like" or "not_like" && val is string s) val = $"%{s}%";
                    var p = cmd.CreateParameter();
                    p.ParameterName = $"@f{i++}";
                    p.Value = CellValue.ToParam(val, Dialect);
                    cmd.Parameters.Add(p);
                    parts.Add($"{col} {op} {p.ParameterName}");
                    break;
                }
            }
        }
        return " WHERE " + string.Join(" AND ", parts);
    }

    private string BuildOrder(IReadOnlyList<OrderClause> orderBy, CanonicalTable meta)
    {
        var items = orderBy.Count > 0
            ? orderBy.Select(o => $"{Ddl.Quote(o.Column)} {(o.Descending ? "DESC" : "ASC")}")
            : meta.PrimaryKeyColumns.Select(pk => $"{Ddl.Quote(pk)} ASC"); // 默认按主键排序，保证分页稳定
        var list = items.ToList();
        return list.Count == 0 ? "" : " ORDER BY " + string.Join(", ", list);
    }

    // ------------------------------------------------ 变更集 ------------------------------------------------

    public virtual async Task<ApplyChangesResponse> ApplyChangesAsync(
        TableRef table, CanonicalTable meta, RowChangeSet changes, CancellationToken ct = default)
    {
        if (Spec.ReadOnly) throw new ArkException(ArkErrorCodes.ReadOnlyMode, "连接处于只读模式，无法提交修改", 403);
        if (meta.PrimaryKeyColumns.Count == 0 && (changes.Updates.Count > 0 || changes.Deletes.Count > 0))
            throw new ArkException(ArkErrorCodes.RowChangeFailed, "该表没有主键，无法定位行进行更新/删除");

        var conn = await GetConnAsync(table.Database, ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int inserted = 0, updated = 0, deleted = 0;
        try
        {
            foreach (var ins in changes.Inserts) inserted += await ExecuteInsert(table, meta, ins, tx, ct);
            foreach (var upd in changes.Updates) updated += await ExecuteUpdate(table, meta, upd, tx, ct);
            foreach (var del in changes.Deletes) deleted += await ExecuteDelete(table, meta, del, tx, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        return new ApplyChangesResponse(inserted, updated, deleted);
    }

    private async Task<int> ExecuteInsert(TableRef table, CanonicalTable meta, RowInsert ins, DbTransaction tx, CancellationToken ct)
    {
        var cols = ins.Values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (cols.Count == 0) return 0;
        foreach (var c in cols) Identifier.EnsureValid(c, "列名");
        await using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        var names = string.Join(", ", cols.Select(Ddl.Quote));
        var ps = new List<string>();
        foreach (var c in cols)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@v" + ps.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(ins.Values[c], TypeOf(meta, c)), Dialect);
            cmd.Parameters.Add(p);
            ps.Add(p.ParameterName);
        }
        cmd.CommandText = $"INSERT INTO {Ddl.TableName(table)} ({names}) VALUES ({string.Join(", ", ps)})";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ExecuteUpdate(TableRef table, CanonicalTable meta, RowUpdate upd, DbTransaction tx, CancellationToken ct)
    {
        if (upd.Values.Count == 0) return 0;
        await using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        var sets = new List<string>();
        foreach (var (col, v) in upd.Values)
        {
            if (meta.PrimaryKeyColumns.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;
            Identifier.EnsureValid(col, "列名");
            var p = cmd.CreateParameter();
            p.ParameterName = "@s" + sets.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(v, TypeOf(meta, col)), Dialect);
            cmd.Parameters.Add(p);
            sets.Add($"{Ddl.Quote(col)} = {p.ParameterName}");
        }
        var where = BuildKeyWhere(meta, upd.Key, cmd);
        if (sets.Count == 0 || where.Length == 0) return 0;
        cmd.CommandText = $"UPDATE {Ddl.TableName(table)} SET {string.Join(", ", sets)} WHERE {where}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ExecuteDelete(TableRef table, CanonicalTable meta, RowDelete del, DbTransaction tx, CancellationToken ct)
    {
        await using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        var where = BuildKeyWhere(meta, del.Key, cmd);
        if (where.Length == 0) return 0;
        cmd.CommandText = $"DELETE FROM {Ddl.TableName(table)} WHERE {where}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private string BuildKeyWhere(CanonicalTable meta, IReadOnlyDictionary<string, JsonElement> key, DbCommand cmd)
    {
        var parts = new List<string>();
        foreach (var (col, v) in key)
        {
            Identifier.EnsureValid(col, "主键列名");
            var p = cmd.CreateParameter();
            p.ParameterName = "@k" + parts.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(v, TypeOf(meta, col)), Dialect);
            cmd.Parameters.Add(p);
            parts.Add($"{Ddl.Quote(col)} = {p.ParameterName}");
        }
        return string.Join(" AND ", parts);
    }

    private static CanonicalType TypeOf(CanonicalTable meta, string column) =>
        meta.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Type
        ?? CanonicalType.Str;

    // ------------------------------------------------ 估算行数 ------------------------------------------------

    public virtual async Task<long> EstimateRowsAsync(TableRef table, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(table.Database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {Ddl.TableName(table)}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    // ------------------------------------------------ 同步运行时（默认实现：不支持） ------------------------------------------------

    /// <summary>批量装载期间的会话调优语句（写连接专用，同步前执行）。</summary>
    public virtual IReadOnlyList<string> BulkLoadPragmas => [];

    public virtual Task<string?> GetViewDefinitionAsync(
        string database, string? schema, string view, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public virtual Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default) =>
        throw new NotSupportedException($"方言 {Dialect} 不支持一致快照会话");

    public virtual Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default) =>
        throw new NotSupportedException($"方言 {Dialect} 不支持批量写入通道");

    // ------------------------------------------------ 函数依赖检查 ------------------------------------------------

    public virtual Task<IReadOnlyList<ExtensionDependency>> CheckFunctionsAsync(
        string database, IReadOnlyList<string> functions, CancellationToken ct = default)
    {
        IReadOnlyList<ExtensionDependency> result = functions
            .Select(f => new ExtensionDependency
            {
                Name = f,
                InstallSql = "",
                Present = BuiltinFunctions.Contains(f),
                Note = BuiltinFunctions.Contains(f) ? null : $"方言 {Dialect} 未能确认支持函数 {f}",
            })
            .ToArray();
        return Task.FromResult(result);
    }

    // ------------------------------------------------ 抽象元数据 ------------------------------------------------

    public abstract Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(string database, CancellationToken ct = default);
    public abstract Task<IReadOnlyList<TableSummary>> ListTablesAsync(string database, string? schema, CancellationToken ct = default);
    public abstract Task<CanonicalTable> GetTableAsync(string database, string? schema, string table, CancellationToken ct = default);
    public abstract Task<string?> GetCreateTableSqlAsync(string database, string? schema, string table, CancellationToken ct = default);

    public abstract Task<string> TestAsync(CancellationToken ct = default);

    // ------------------------------------------------ 释放 ------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var c in _connections.Values)
            await c.DisposeAsync();
        _connections.Clear();
        GC.SuppressFinalize(this);
    }

    protected static string Compact(string sql)
    {
        var s = sql.Trim().Replace('\n', ' ');
        return s.Length <= 120 ? s : s[..120] + "…";
    }
}
