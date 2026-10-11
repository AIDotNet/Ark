using System.Data;
using System.Data.Common;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using global::DuckDB.NET.Data;

namespace Ark.Providers.DuckDB;

/// <summary>DuckDB 提供者：嵌入式文件库，元数据取自目录表函数（duckdb_columns/duckdb_constraints/duckdb_indexes）。</summary>
public sealed class DuckDbProvider : ProviderBase
{
    public DuckDbProvider(ConnectionSpec spec) : base(spec) { }

    public override ArkDialect Dialect => ArkDialect.DuckDB;

    public override DbCapabilities Capabilities { get; } =
        new(SupportsSchemas: false, SupportsMultipleDatabases: true, SupportsTruncate: true,
            SupportsDropColumn: true, SupportsAlterColumnType: true, SupportsIndexConcurrently: false,
            SupportsBulkCopy: true, SupportsConsistentSnapshot: true, ServerKind: "DuckDB",
            InlineForeignKeys: true);

    public override IDdlGenerator Ddl { get; } = new DuckDbDdlGenerator();

    protected override DbConnection CreateConnection(string? database)
    {
        var path = Spec.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "DuckDB 连接缺少文件路径");
        // 文件型单库：database 参数（ATTACH 的附加库）不在连接串上表达
        var b = new DuckDBConnectionStringBuilder
        {
            DataSource = path,
            AccessMode = Spec.ReadOnly ? DuckDBAccessMode.ReadOnly : DuckDBAccessMode.Automatic,
        };
        return new DuckDBConnection(b.ConnectionString);
    }

    public override async Task<string> TestAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version()";
        return (string?)(await cmd.ExecuteScalarAsync(ct)) ?? "DuckDB";
    }

    public override Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DatabaseInfo>>([new DatabaseInfo("main")]);

    public override Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(string database, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SchemaInfo>>([new SchemaInfo("main")]);

    public override async Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT table_name, table_type FROM information_schema.tables " +
            "WHERE table_schema = 'main' ORDER BY table_name";
        var list = new List<TableSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new TableSummary(reader.GetString(0), reader.GetString(1) == "VIEW" ? "view" : "table", null));
        return list;
    }

    public override async Task<CanonicalTable> GetTableAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        var conn = await GetConnAsync(null, ct);

        var isView = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT table_type FROM information_schema.tables WHERE table_schema = 'main' AND table_name = ?";
            cmd.Parameters.Add(CreateParam(cmd, table));
            var t = await cmd.ExecuteScalarAsync(ct) as string
                    ?? throw new ArkException(ArkErrorCodes.TableNotFound, $"表 {table} 不存在", 404);
            isView = t == "VIEW";
        }

        // 列（column_default / is_nullable / data_type 均可从 duckdb_columns() 一次取全）
        var columns = new List<CanonicalColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT column_name, data_type, is_nullable, column_default, character_maximum_length, " +
                "       numeric_precision, numeric_scale " +
                "FROM duckdb_columns() WHERE table_name = ? AND schema_name = 'main' ORDER BY column_index";
            cmd.Parameters.Add(CreateParam(cmd, table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var dataType = r.GetString(1);
                var nullable = r.GetBoolean(2);
                var dflt = r.IsDBNull(3) ? null : r.GetString(3);
                var len = r.IsDBNull(4) ? (int?)null : r.GetInt32(4);
                var prec = r.IsDBNull(5) ? (int?)null : r.GetInt32(5);
                var scale = r.IsDBNull(6) ? (int?)null : r.GetInt32(6);
                columns.Add(new CanonicalColumn
                {
                    Name = name,
                    Type = TypeMapper.ToCanonical(ArkDialect.DuckDB, dataType, len, prec, scale),
                    Nullable = nullable,
                    DefaultValueSql = dflt,
                    DefaultKind = FunctionTranslator.Recognize(ArkDialect.DuckDB, dflt),
                    // DuckDB 无 AUTOINCREMENT；IDENTITY/序列列为将来事
                    IsAutoIncrement = false,
                });
            }
        }

        // 约束：主键 / 外键 / UNIQUE（DuckDB 的 UNIQUE 约束不出现在 duckdb_indexes，需单独读）
        var pkColumns = new List<string>();
        var fks = new List<CanonicalForeignKey>();
        var uniqueIndexes = new List<CanonicalIndex>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT constraint_type, constraint_name, constraint_column_names, referenced_table, referenced_column_names " +
                "FROM duckdb_constraints() WHERE table_name = ? AND schema_name = 'main' ORDER BY constraint_index";
            cmd.Parameters.Add(CreateParam(cmd, table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var type = r.GetString(0);
                var name = r.GetString(1);
                var cols = ReadStringList(r.GetValue(2));
                switch (type)
                {
                    case "PRIMARY KEY":
                        pkColumns.AddRange(cols);
                        break;
                    case "FOREIGN KEY":
                        var refTable = r.IsDBNull(3) ? "" : r.GetString(3);
                        // 引用列拿不到时保留空 FK（列信息缺失不阻塞结构同步）
                        fks.Add(new CanonicalForeignKey
                        {
                            Name = name,
                            Columns = cols,
                            ReferencedTable = refTable,
                            ReferencedColumns = ReadStringList(r.GetValue(4)),
                        });
                        break;
                    case "UNIQUE":
                        uniqueIndexes.Add(new CanonicalIndex
                        {
                            Name = name,
                            Columns = cols,
                            IsUnique = true,
                            IsPrimaryKey = false,
                        });
                        break;
                }
            }
        }
        columns = columns.Select(c => c with { IsPrimaryKey = pkColumns.Contains(c.Name) }).ToList();

        // 索引（排除主键自动索引；唯一索引用显式名保留唯一性）
        var indexes = new List<CanonicalIndex>(uniqueIndexes);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT index_name, is_unique, expressions FROM duckdb_indexes() " +
                "WHERE table_name = ? AND schema_name = 'main' AND NOT is_primary";
            cmd.Parameters.Add(CreateParam(cmd, table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var cols = ParseIndexExpressions(r.IsDBNull(2) ? "" : r.GetString(2));
                if (cols.Count == 0) continue;
                indexes.Add(new CanonicalIndex
                {
                    Name = r.GetString(0),
                    Columns = cols,
                    IsUnique = r.GetBoolean(1),
                    IsPrimaryKey = false,
                });
            }
        }

        return new CanonicalTable
        {
            Name = table,
            Columns = columns,
            PrimaryKeyColumns = pkColumns,
            Indexes = indexes,
            ForeignKeys = fks,
            IsView = isView,
        };
    }

    public override async Task<string?> GetCreateTableSqlAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM duckdb_tables() WHERE table_name = ? AND schema_name = 'main' AND NOT internal";
        cmd.Parameters.Add(CreateParam(cmd, table));
        var sql = await cmd.ExecuteScalarAsync(ct) as string;
        if (sql is not null) return sql;
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT sql FROM duckdb_views() WHERE view_name = ? AND schema_name = 'main' AND NOT internal";
        cmd2.Parameters.Add(CreateParam(cmd2, table));
        return await cmd2.ExecuteScalarAsync(ct) as string;
    }

    public override Task<ExplainResponse> ExplainAsync(
        string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        // EXPLAIN 输出 explain_key | explain_value 两列，仅保留计划文本
        return ReadPlanAsync(database, "EXPLAIN " + sql, sql, timeoutSeconds, ct,
            r => r.FieldCount >= 2 && !r.IsDBNull(1) ? r.GetValue(1)?.ToString() ?? "" : r.GetValue(0)?.ToString() ?? "");
    }

    public override Task<IReadOnlyList<ExtensionDependency>> CheckFunctionsAsync(
        string database, IReadOnlyList<string> functions, CancellationToken ct = default)
    {
        // DuckDB 内置 uuid()/coalesce()/concat() 等，无需扩展安装：全部视为可用
        IReadOnlyList<ExtensionDependency> result = functions
            .Select(f => new ExtensionDependency { Name = f, InstallSql = "", Present = true })
            .ToArray();
        return Task.FromResult(result);
    }

    // ------------------------------------------------ 同步运行时 ------------------------------------------------

    public override async Task<string?> GetViewDefinitionAsync(
        string database, string? schema, string view, CancellationToken ct = default)
    {
        Identifier.EnsureValid(view, "视图名");
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM duckdb_views() WHERE view_name = ? AND schema_name = 'main' AND NOT internal";
        cmd.Parameters.Add(CreateParam(cmd, view));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public override async Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default)
    {
        // DuckDB.NET 驱动仅接受 Unspecified/Snapshot：DuckDB 本身为 MVCC 快照隔离
        var conn = CreateConnection(database);
        await conn.OpenAsync(ct);
        var tx = await conn.BeginTransactionAsync(IsolationLevel.Snapshot, ct);
        return new DuckDbSnapshotSession(conn, tx);
    }

    private sealed class DuckDbSnapshotSession(DbConnection conn, DbTransaction tx) : ISnapshotSession
    {
        public DbConnection Connection => conn;

        public string Description => "BEGIN（snapshot 事务）";

        public async ValueTask DisposeAsync()
        {
            try { await tx.RollbackAsync(CancellationToken.None); }
            catch { /* 尽力回滚 */ }
            await conn.DisposeAsync();
        }
    }

    public override async Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default)
    {
        var conn = await OpenConnectionAsync(table.Database, ct);
        return new DuckDbBatchWriter(conn, Ddl.TableName(table), columns);
    }

    /// <summary>DuckDB 批量写入通道：单事务 + 多值 INSERT 分批（嵌入式库无原生网络协议，批量 INSERT 已足够快）。</summary>
    private sealed class DuckDbBatchWriter(DbConnection conn, string tableName, IReadOnlyList<string> columns) : IBulkWriter
    {
        private const int BatchRows = 500;
        private readonly List<object?[]> _buffer = [];
        private DbTransaction? _tx;

        public long RowsWritten { get; private set; }

        public string Channel => "批量 INSERT（单事务）";

        public async Task WriteAsync(object?[] values, CancellationToken ct = default)
        {
            _buffer.Add(values);
            if (_buffer.Count >= BatchRows) await FlushAsync(ct);
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            await FlushAsync(ct);
            if (_tx is not null)
            {
                await _tx.CommitAsync(ct);
                await _tx.DisposeAsync();
                _tx = null;
            }
        }

        private async Task FlushAsync(CancellationToken ct)
        {
            if (_buffer.Count == 0) return;
            _tx ??= await conn.BeginTransactionAsync(ct);
            var colSql = string.Join(", ", columns.Select(c => $"\"{c.Replace("\"", "\"\"")}\""));
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = _tx;
            var groups = new List<string>();
            var pi = 0;
            foreach (var row in _buffer)
            {
                var ps = new List<string>();
                for (var c = 0; c < columns.Count; c++)
                {
                    var p = cmd.CreateParameter();
                    // DuckDB 仅绑定 $name 标记（@name 会被解析为标识符，? 不支持参数集合绑定）
                    p.ParameterName = $"b{pi++}";
                    p.Value = CellValue.ToParam(row[c], ArkDialect.DuckDB);
                    cmd.Parameters.Add(p);
                    ps.Add("$" + p.ParameterName);
                }
                groups.Add("(" + string.Join(", ", ps) + ")");
            }
            cmd.CommandText = $"INSERT INTO {tableName} ({colSql}) VALUES {string.Join(", ", groups)}";
            await cmd.ExecuteNonQueryAsync(ct);
            RowsWritten += _buffer.Count;
            _buffer.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_buffer.Count > 0) await FlushAsync(CancellationToken.None);
                if (_tx is not null)
                {
                    await _tx.CommitAsync();
                    await _tx.DisposeAsync();
                }
            }
            catch { /* 尽力提交 */ }
            await conn.DisposeAsync();
        }
    }

    // ------------------------------------------------ 变更集（$name 参数绑定） ------------------------------------------------

    /// <summary>
    /// 复写变更集提交：ProviderBase 生成 @name 占位符，DuckDB 解析为标识符（Binder Error），
    /// 仅 $name 标记可绑定，其余逻辑（单事务、失败回滚）与基类一致。
    /// </summary>
    public override async Task<ApplyChangesResponse> ApplyChangesAsync(
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
            foreach (var ins in changes.Inserts) inserted += await ExecuteInsert(conn, tx, table, meta, ins, ct);
            foreach (var upd in changes.Updates) updated += await ExecuteUpdate(conn, tx, table, meta, upd, ct);
            foreach (var del in changes.Deletes) deleted += await ExecuteDelete(conn, tx, table, meta, del, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        return new ApplyChangesResponse(inserted, updated, deleted);
    }

    private async Task<int> ExecuteInsert(
        DbConnection conn, DbTransaction tx, TableRef table, CanonicalTable meta, RowInsert ins, CancellationToken ct)
    {
        var cols = ins.Values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (cols.Count == 0) return 0;
        foreach (var c in cols) Identifier.EnsureValid(c, "列名");
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var names = string.Join(", ", cols.Select(Ddl.Quote));
        var ps = new List<string>();
        foreach (var c in cols)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "v" + ps.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(ins.Values[c], TypeOf(meta, c)), Dialect);
            cmd.Parameters.Add(p);
            ps.Add("$" + p.ParameterName);
        }
        cmd.CommandText = $"INSERT INTO {Ddl.TableName(table)} ({names}) VALUES ({string.Join(", ", ps)})";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ExecuteUpdate(
        DbConnection conn, DbTransaction tx, TableRef table, CanonicalTable meta, RowUpdate upd, CancellationToken ct)
    {
        if (upd.Values.Count == 0) return 0;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var sets = new List<string>();
        foreach (var (col, v) in upd.Values)
        {
            if (meta.PrimaryKeyColumns.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;
            Identifier.EnsureValid(col, "列名");
            var p = cmd.CreateParameter();
            p.ParameterName = "s" + sets.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(v, TypeOf(meta, col)), Dialect);
            cmd.Parameters.Add(p);
            sets.Add($"{Ddl.Quote(col)} = ${p.ParameterName}");
        }
        var where = BuildKeyWhere(cmd, meta, upd.Key);
        if (sets.Count == 0 || where.Length == 0) return 0;
        cmd.CommandText = $"UPDATE {Ddl.TableName(table)} SET {string.Join(", ", sets)} WHERE {where}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ExecuteDelete(
        DbConnection conn, DbTransaction tx, TableRef table, CanonicalTable meta, RowDelete del, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var where = BuildKeyWhere(cmd, meta, del.Key);
        if (where.Length == 0) return 0;
        cmd.CommandText = $"DELETE FROM {Ddl.TableName(table)} WHERE {where}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>主键定位 WHERE 片段（$name 绑定）。</summary>
    private string BuildKeyWhere(DbCommand cmd, CanonicalTable meta, IReadOnlyDictionary<string, System.Text.Json.JsonElement> key)
    {
        var parts = new List<string>();
        foreach (var (col, v) in key)
        {
            Identifier.EnsureValid(col, "主键列名");
            var p = cmd.CreateParameter();
            p.ParameterName = "k" + parts.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(v, TypeOf(meta, col)), Dialect);
            cmd.Parameters.Add(p);
            parts.Add($"{Ddl.Quote(col)} = ${p.ParameterName}");
        }
        return string.Join(" AND ", parts);
    }

    private static CanonicalType TypeOf(CanonicalTable meta, string column) =>
        meta.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Type
        ?? CanonicalType.Str;

    // ------------------------------------------------ 行读取（复合类型读路径） ------------------------------------------------

    /// <summary>
    /// 复写行读取：DuckDB 的 LIST/STRUCT/MAP 复合类型会让驱动直接返回数组/结构体对象（JSON 序列化失败），
    /// 对 data_type 属于复合类型的列（以 duckdb_columns() 为准）包一层 CAST(col AS VARCHAR)；其余照 ProviderBase 逻辑。
    /// </summary>
    public override async Task<RowPage> ReadRowsAsync(
        TableRef table, CanonicalTable meta, RowQueryRequest query, CancellationToken ct = default)
    {
        foreach (var f in query.Filters) Identifier.EnsureValid(f.Column, "筛选列名");
        foreach (var o in query.OrderBy) Identifier.EnsureValid(o.Column, "排序列名");

        var knownCols = meta.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in query.Filters)
            if (!knownCols.Contains(f.Column))
                throw ArkException.Validation($"列 {f.Column} 不存在");
        foreach (var o in query.OrderBy)
            if (!knownCols.Contains(o.Column))
                throw ArkException.Validation($"列 {o.Column} 不存在");

        var conn = await GetConnAsync(table.Database, ct);
        await using var cmd = conn.CreateCommand();

        // 先取复合类型列名（canonical 中 VARCHAR[] 等括号写法映射 Unknown，不能靠 Json 判定）
        var compositeCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText =
                "SELECT column_name, data_type FROM duckdb_columns() WHERE table_name = ? AND schema_name = 'main'";
            metaCmd.Parameters.Add(CreateParam(metaCmd, table.Table));
            await using var r = await metaCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (IsCompositeDuckDbType(r.GetString(1)))
                    compositeCols.Add(r.GetString(0));
        }

        var where = BuildWhere(query.Filters, cmd, meta);
        var order = BuildOrderLocal(query.OrderBy, meta);

        cmd.CommandText = $"SELECT COUNT(*) FROM {Ddl.TableName(table)}{where}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        // 复合类型列以 VARCHAR 文本读出，其余列照常
        var projection = meta.Columns.Select(c => compositeCols.Contains(c.Name)
            ? $"CAST({Ddl.Quote(c.Name)} AS VARCHAR) AS {Ddl.Quote(c.Name)}"
            : Ddl.Quote(c.Name));
        cmd.CommandText = $"SELECT {string.Join(", ", projection)} FROM {Ddl.TableName(table)}{where}{order} LIMIT {query.Limit} OFFSET {query.Offset}";
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
            NormalizeBinary(row);
            rows.Add(row);
        }
        return new RowPage(cols, rows, total, query.Limit, query.Offset, meta.PrimaryKeyColumns);
    }

    /// <summary>DuckDB.NET 的 BLOB 返回 UnmanagedMemoryStream，统一转 byte[]（对齐其余方言的读取形状）。</summary>
    private static void NormalizeBinary(object?[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] is System.IO.UnmanagedMemoryStream s)
            {
                using var ms = new System.IO.MemoryStream();
                s.CopyTo(ms);
                row[i] = ms.ToArray();
            }
        }
    }

    /// <summary>与 ProviderBase.BuildOrder 一致（该方法为 private 无法复用）：默认按主键排序保证分页稳定。</summary>
    private static string BuildOrderLocal(IReadOnlyList<OrderClause> orderBy, CanonicalTable meta)
    {
        var items = orderBy.Count > 0
            ? orderBy.Select(o => $"{QuoteIdent(o.Column)} {(o.Descending ? "DESC" : "ASC")}")
            : meta.PrimaryKeyColumns.Select(pk => $"{QuoteIdent(pk)} ASC");
        var list = items.ToList();
        return list.Count == 0 ? "" : " ORDER BY " + string.Join(", ", list);
    }

    private static string QuoteIdent(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// duckdb_columns().data_type 是否复合类型：LIST/STRUCT/MAP/UNION/ENUM 前缀，或方括号写法（如 VARCHAR[]）。
    /// </summary>
    private static bool IsCompositeDuckDbType(string dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var s = dataType.Trim();
        if (s.EndsWith("[]")) return true;
        var paren = s.IndexOf('(');
        var bare = (paren > 0 ? s[..paren] : s).Trim().ToLowerInvariant();
        return bare is "struct" or "map" or "list" or "union" or "enum" or "array";
    }

    // ------------------------------------------------ 工具 ------------------------------------------------

    private static DbParameter CreateParam(DbCommand cmd, object value)
    {
        var p = cmd.CreateParameter();
        p.Value = value;
        return p;
    }

    /// <summary>驱动把 LIST(VARCHAR) 列返回为 List&lt;string&gt;（约束列名列表）。</summary>
    private static IReadOnlyList<string> ReadStringList(object? value) => value switch
    {
        System.Collections.IEnumerable en when value is not string =>
            en.Cast<object>().Where(v => v is not null).Select(v => v.ToString() ?? "").ToList(),
        _ => [],
    };

    /// <summary>duckdb_indexes().expressions 形如 ['"col1"', '"col2"']，解析为裸列名列表。</summary>
    private static List<string> ParseIndexExpressions(string expressions)
    {
        var s = expressions.Trim();
        if (s.StartsWith('[') && s.EndsWith(']')) s = s[1..^1];
        return s
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim().Trim('\'', '"').Replace("\"\"", "\""))
            .ToList();
    }
}
