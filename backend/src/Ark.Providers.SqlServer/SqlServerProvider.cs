using System.Data;
using System.Data.Common;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using Microsoft.Data.SqlClient;

namespace Ark.Providers.SqlServer;

/// <summary>SQL Server Provider：元数据全部走 sys.* 目录视图，行分页用 OFFSET/FETCH，批量写用 SqlBulkCopy。</summary>
public sealed class SqlServerProvider : ProviderBase
{
    public SqlServerProvider(ConnectionSpec spec) : base(spec) { }

    public override ArkDialect Dialect => ArkDialect.SqlServer;

    public override DbCapabilities Capabilities { get; } =
        new(SupportsSchemas: true, SupportsMultipleDatabases: true, SupportsTruncate: true,
            SupportsDropColumn: true, SupportsAlterColumnType: true, SupportsIndexConcurrently: false,
            SupportsBulkCopy: true, SupportsConsistentSnapshot: false, ServerKind: "SQL Server");

    public override IDdlGenerator Ddl { get; } = new SqlServerDdlGenerator();

    protected override DbConnection CreateConnection(string? database)
    {
        if (string.IsNullOrWhiteSpace(Spec.Host))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "SQL Server 连接缺少主机地址");
        var b = new SqlConnectionStringBuilder
        {
            DataSource = $"{Spec.Host},{Spec.Port ?? 1433}",
            InitialCatalog = string.IsNullOrEmpty(database) ? (Spec.Database ?? "master") : database,
            UserID = Spec.Username ?? "sa",
            Password = Spec.Password ?? "",
            // 容器/自签证书环境必须 Encrypt=Optional，强制加密会握手失败
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        };
        return new SqlConnection(b.ConnectionString);
    }

    public override async Task<string> TestAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@VERSION";
        return (string?)(await cmd.ExecuteScalarAsync(ct)) ?? "SQL Server";
    }

    public override async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        // database_id <= 4 为系统库（master/model/msdb/tempdb）
        cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name";
        var list = new List<DatabaseInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new DatabaseInfo(reader.GetString(0)));
        return list;
    }

    public override async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(string database, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM sys.schemas WHERE name NOT IN " +
            "('sys','guest','INFORMATION_SCHEMA','db_accessadmin','db_backupoperator','db_datareader'," +
            "'db_datawriter','db_ddladmin','db_denydatareader','db_denydatawriter','db_owner','db_securityadmin') " +
            "ORDER BY name";
        var list = new List<SchemaInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new SchemaInfo(reader.GetString(0)));
        return list;
    }

    public override async Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        Identifier.EnsureValid(schema ?? "dbo", "schema 名");
        var conn = await GetConnAsync(database, ct);
        var s = schema ?? "dbo";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT t.name, 'table', " +
            "       (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1)) " +
            "FROM sys.tables t JOIN sys.schemas sc ON sc.schema_id = t.schema_id WHERE sc.name = @s " +
            "UNION ALL " +
            "SELECT v.name, 'view', NULL " +
            "FROM sys.views v JOIN sys.schemas sc ON sc.schema_id = v.schema_id WHERE sc.name = @s " +
            "ORDER BY 1";
        cmd.Parameters.Add(new SqlParameter("@s", s));
        var list = new List<TableSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long? rows = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            list.Add(new TableSummary(reader.GetString(0), reader.GetString(1), rows < 0 ? null : rows));
        }
        return list;
    }

    public override async Task<CanonicalTable> GetTableAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        Identifier.EnsureValid(schema ?? "dbo", "schema 名");
        var conn = await GetConnAsync(database, ct);
        var s = schema ?? "dbo";
        var obj = $"{s}.{table}";

        var isView = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT type FROM sys.objects o JOIN sys.schemas sc ON sc.schema_id = o.schema_id " +
                "WHERE sc.name = @s AND o.name = @t AND o.type IN ('U','V')";
            cmd.Parameters.Add(new SqlParameter("@s", s));
            cmd.Parameters.Add(new SqlParameter("@t", table));
            var type = await cmd.ExecuteScalarAsync(ct) as string
                       ?? throw new ArkException(ArkErrorCodes.TableNotFound, $"表 {s}.{table} 不存在", 404);
            isView = type == "V";
        }

        // 列（TYPE_NAME 给小写系统类型名；nvarchar/nchar 的 max_length 是字节数需换算字符数，-1 表示 MAX）
        var columns = new List<CanonicalColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.name, TYPE_NAME(c.system_type_id) AS type_name, c.max_length, c.precision, c.scale, " +
                "       c.is_nullable, c.is_identity, c.is_computed, " +
                "       dc.definition AS default_sql, cc.definition AS computed_sql " +
                "FROM sys.columns c " +
                "LEFT JOIN sys.default_constraints dc " +
                "       ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id " +
                "LEFT JOIN sys.computed_columns cc " +
                "       ON cc.object_id = c.object_id AND cc.column_id = c.column_id " +
                "WHERE c.object_id = OBJECT_ID(@obj) ORDER BY c.column_id";
            cmd.Parameters.Add(new SqlParameter("@obj", obj));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var rawType = r.GetString(1).ToLowerInvariant();
                var maxLength = r.GetInt16(2);
                int? precision = r.GetByte(3) == 0 ? null : r.GetByte(3);
                int? scale = r.GetByte(4) == 0 ? null : r.GetByte(4);
                var nullable = r.GetBoolean(5);
                var isIdentity = r.GetBoolean(6);
                var isComputed = r.GetBoolean(7);
                var dflt = r.IsDBNull(8) ? null : r.GetString(8);
                var computedExpr = r.IsDBNull(9) ? null : r.GetString(9);

                // max_length 换算：nvarchar/nchar 按字节存储 → 字符数 = max_length/2；-1 = MAX（类型名带 (max) 交给 TypeMapper）
                int? len;
                if (maxLength == -1)
                {
                    rawType += "(max)";
                    len = null;
                }
                else
                {
                    len = rawType is "nvarchar" or "nchar" ? maxLength / 2 : maxLength;
                }

                columns.Add(new CanonicalColumn
                {
                    Name = name,
                    Type = TypeMapper.ToCanonical(ArkDialect.SqlServer, rawType, len, precision, scale),
                    Nullable = nullable,
                    DefaultValueSql = dflt,
                    DefaultKind = isComputed || isIdentity
                        ? ValueExprKind.None
                        : FunctionTranslator.Recognize(ArkDialect.SqlServer, dflt),
                    IsAutoIncrement = isIdentity,
                    IsPrimaryKey = false,
                    IsGenerated = isComputed,
                    GeneratedExpression = computedExpr,
                });
            }
        }

        // 主键（有序）
        var pkColumns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.name FROM sys.key_constraints kc " +
                "JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id " +
                "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                "WHERE kc.type = 'PK' AND kc.parent_object_id = OBJECT_ID(@obj) ORDER BY ic.key_ordinal";
            cmd.Parameters.Add(new SqlParameter("@obj", obj));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) pkColumns.Add(r.GetString(0));
        }
        columns = columns.Select(c => c with { IsPrimaryKey = pkColumns.Contains(c.Name) }).ToList();

        // 索引（排除主键与唯一约束的内部索引，二者分别由 PK 列集与唯一约束表达）
        var indexes = new List<CanonicalIndex>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT i.name, i.is_unique, c.name FROM sys.indexes i " +
                "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                "WHERE i.object_id = OBJECT_ID(@obj) AND i.index_id > 1 " +
                "  AND i.is_primary_key = 0 AND i.is_unique_constraint = 0 AND i.name IS NOT NULL " +
                "  AND ic.is_included_column = 0 " +
                "ORDER BY i.name, ic.key_ordinal";
            cmd.Parameters.Add(new SqlParameter("@obj", obj));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byName = new Dictionary<string, (bool Unique, List<string> Cols)>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                if (!byName.TryGetValue(name, out var e))
                {
                    e = (r.GetBoolean(1), []);
                    byName[name] = e;
                }
                e.Cols.Add(r.GetString(2));
            }
            indexes.AddRange(byName.Select(kv => new CanonicalIndex
            {
                Name = kv.Key,
                Columns = kv.Value.Cols,
                IsUnique = kv.Value.Unique,
                IsPrimaryKey = false,
            }));
        }

        // 外键（delete/update_referential_action_desc 形如 NO_ACTION/SET_NULL → 换算为 NO ACTION/SET NULL）
        var fks = new List<CanonicalForeignKey>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT fk.name, pc.name, rt.name, rc.name, " +
                "       fk.delete_referential_action_desc, fk.update_referential_action_desc " +
                "FROM sys.foreign_keys fk " +
                "JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id " +
                "JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id " +
                "JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id " +
                "JOIN sys.tables rt ON rt.object_id = fkc.referenced_object_id " +
                "WHERE fk.parent_object_id = OBJECT_ID(@obj) " +
                "ORDER BY fk.name, fkc.constraint_column_id";
            cmd.Parameters.Add(new SqlParameter("@obj", obj));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byName = new Dictionary<string, (string RefTable, string? OnUpdate, string? OnDelete, List<string> Cols, List<string> RefCols)>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var col = r.GetString(1);
                var refTable = r.GetString(2);
                var refCol = r.GetString(3);
                var onDelete = r.IsDBNull(4) ? null : r.GetString(4)?.Replace('_', ' ');
                var onUpdate = r.IsDBNull(5) ? null : r.GetString(5)?.Replace('_', ' ');
                if (!byName.TryGetValue(name, out var e))
                {
                    e = (refTable, onUpdate, onDelete, [], []);
                    byName[name] = e;
                }
                e.Cols.Add(col);
                e.RefCols.Add(refCol);
            }
            fks.AddRange(byName.Select(kv => new CanonicalForeignKey
            {
                Name = kv.Key,
                Columns = kv.Value.Cols,
                ReferencedTable = kv.Value.RefTable,
                ReferencedColumns = kv.Value.RefCols,
                OnUpdate = kv.Value.OnUpdate,
                OnDelete = kv.Value.OnDelete,
            }));
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
        Identifier.EnsureValid(schema ?? "dbo", "schema 名");
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(@obj)";
        cmd.Parameters.Add(new SqlParameter("@obj", $"{schema ?? "dbo"}.{table}"));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public override async Task<long> EstimateRowsAsync(TableRef table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table.Table, "表名");
        Identifier.EnsureValid(table.Schema ?? "dbo", "schema 名");
        var conn = await GetConnAsync(table.Database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ISNULL(SUM(row_count), 0) FROM sys.dm_db_partition_stats " +
            "WHERE object_id = OBJECT_ID(@obj) AND index_id IN (0, 1)";
        cmd.Parameters.Add(new SqlParameter("@obj", $"{table.Schema ?? "dbo"}.{table.Table}"));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    // ------------------------------------------------ SQL 查询 ------------------------------------------------

    /// <summary>
    /// SQL Server 不支持 LIMIT/OFFSET：改写为 OFFSET ... ROWS FETCH NEXT ... ROWS ONLY；
    /// FETCH 要求必须有 ORDER BY，无排序列时按主键排序（无主键则 ORDER BY (SELECT NULL) 保证语法成立，行序不保证稳定）。
    /// </summary>
    public override async Task<RowPage> ReadRowsAsync(
        TableRef table, CanonicalTable meta, RowQueryRequest query, CancellationToken ct = default)
    {
        foreach (var f in query.Filters) Identifier.EnsureValid(f.Column, "筛选列名");
        foreach (var o in query.OrderBy) Identifier.EnsureValid(o.Column, "排序列名");

        // 过滤/排序列必须是表内真实列（对齐 ProviderBase 共享实现）
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
        var order = BuildOrderClause(query.OrderBy, meta);

        cmd.CommandText = $"SELECT COUNT(*) FROM {Ddl.TableName(table)}{where}";
        var total = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        cmd.CommandText =
            $"SELECT * FROM {Ddl.TableName(table)}{where}{order} " +
            $"OFFSET {query.Offset} ROWS FETCH NEXT {query.Limit} ROWS ONLY";
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

    /// <summary>排序子句：显式排序列优先，缺省主键 ASC，再缺省 ORDER BY (SELECT NULL)（FETCH 语法要求）。</summary>
    private string BuildOrderClause(IReadOnlyList<OrderClause> orderBy, CanonicalTable meta)
    {
        var items = orderBy.Count > 0
            ? orderBy.Select(o => $"{Ddl.Quote(o.Column)} {(o.Descending ? "DESC" : "ASC")}")
            : meta.PrimaryKeyColumns.Select(pk => $"{Ddl.Quote(pk)} ASC");
        var list = items.ToList();
        return list.Count == 0 ? " ORDER BY (SELECT NULL)" : " ORDER BY " + string.Join(", ", list);
    }

    // ------------------------------------------------ 补全元数据 / 执行计划 ------------------------------------------------

    public override async Task<ExplainResponse> ExplainAsync(
        string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        EnsureSelectable(sql);
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = timeoutSeconds;
        // SHOWPLAN_ALL 是连接级开关：置 ON 后执行原语句只返回计划行、不会真正执行，finally 中恢复
        await using (var on = conn.CreateCommand())
        {
            on.CommandText = "SET SHOWPLAN_ALL ON";
            await on.ExecuteNonQueryAsync(ct);
        }
        var lines = new List<string>();
        try
        {
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                ordinals[reader.GetName(i)] = i;
            string Cell(DbDataReader r, string col) =>
                ordinals.TryGetValue(col, out var i) && !r.IsDBNull(i) ? r.GetValue(i)?.ToString() ?? "" : "";
            while (await reader.ReadAsync(ct))
                lines.Add($"{Cell(reader, "NodeId")} | {Cell(reader, "PhysicalOp")} | " +
                          $"{Cell(reader, "LogicalOp")} | {Cell(reader, "TotalSubtreeCost")} | {Cell(reader, "Argument")}");
        }
        finally
        {
            try
            {
                cmd.CommandText = "SET SHOWPLAN_ALL OFF";
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { /* 连接即将被丢弃或已不可用时尽力恢复 */ }
        }
        return new ExplainResponse(string.Join("\n", lines));
    }

    // ------------------------------------------------ 同步运行时 ------------------------------------------------

    public override async Task<string?> GetViewDefinitionAsync(
        string database, string? schema, string view, CancellationToken ct = default)
    {
        Identifier.EnsureValid(view, "视图名");
        Identifier.EnsureValid(schema ?? "dbo", "schema 名");
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT definition FROM sys.sql_modules WHERE object_id = OBJECT_ID(@obj)";
        cmd.Parameters.Add(new SqlParameter("@obj", $"{schema ?? "dbo"}.{view}"));
        try
        {
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    public override Task<IReadOnlyList<ExtensionDependency>> CheckFunctionsAsync(
        string database, IReadOnlyList<string> functions, CancellationToken ct = default) =>
        // SQL Server 内置函数随引擎提供，无扩展依赖概念
        Task.FromResult<IReadOnlyList<ExtensionDependency>>([]);

    public override async Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default)
    {
        var conn = CreateConnection(database);
        await conn.OpenAsync(ct);
        // 先探测数据库是否启用快照隔离（未启用时 BEGIN SNAPSHOT 报 3952），失败回退 READ COMMITTED。
        // 共享同步引擎在会话连接上直接创建命令（不绑定事务），SQL Server 要求命令挂接本地事务，
        // 故探测完立即回滚、不维持打开事务，改为连接级隔离级别 + 如实描述（逐语句一致）。
        var snapshotEnabled = false;
        try
        {
            await using var probe = ((SqlConnection)conn).BeginTransaction(IsolationLevel.Snapshot);
            snapshotEnabled = true;
            await probe.RollbackAsync(ct);
        }
        catch (SqlException)
        {
            snapshotEnabled = false;
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = snapshotEnabled
                ? "SET TRANSACTION ISOLATION LEVEL SNAPSHOT"
                : "SET TRANSACTION ISOLATION LEVEL READ COMMITTED";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new SqlServerSnapshotSession(conn, snapshotEnabled);
    }

    private sealed class SqlServerSnapshotSession(DbConnection conn, bool snapshotEnabled) : ISnapshotSession
    {
        public DbConnection Connection => conn;
        public string Description => snapshotEnabled
            ? "SNAPSHOT（数据库已启用快照隔离；无跨表事务，逐语句一致）"
            : "READ COMMITTED（数据库未启用快照隔离，逐语句一致）";

        public async ValueTask DisposeAsync() => await conn.DisposeAsync();
    }

    public override async Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default)
    {
        var conn = (SqlConnection)await OpenConnectionAsync(table.Database, ct);
        var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        return new SqlBulkCopyWriter(conn, tx, Ddl.TableName(table), columns);
    }

    /// <summary>SqlBulkCopy 写入器：单事务内按批缓冲，经内存 IDataReader 适配器逐批写入。</summary>
    private sealed class SqlBulkCopyWriter(SqlConnection conn, SqlTransaction tx, string tableName, IReadOnlyList<string> columns) : IBulkWriter
    {
        private const int BatchRows = 2000;
        private readonly List<object?[]> _buffer = [];
        private bool _committed;

        public long RowsWritten { get; private set; }

        public string Channel => "SqlBulkCopy";

        public async Task WriteAsync(object?[] values, CancellationToken ct = default)
        {
            _buffer.Add(values);
            if (_buffer.Count >= BatchRows) await FlushAsync(ct);
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            await FlushAsync(ct);
            await tx.CommitAsync(ct);
            _committed = true;
        }

        private async Task FlushAsync(CancellationToken ct)
        {
            if (_buffer.Count == 0) return;
            using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
            {
                DestinationTableName = tableName,
                BulkCopyTimeout = 0,
                BatchSize = BatchRows,
                EnableStreaming = true,
            };
            for (var i = 0; i < columns.Count; i++)
                bulk.ColumnMappings.Add(new SqlBulkCopyColumnMapping(i, columns[i]));
            await bulk.WriteToServerAsync(new RowDataReader(columns, _buffer), ct);
            RowsWritten += _buffer.Count;
            _buffer.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_committed)
                {
                    if (_buffer.Count > 0) await FlushAsync(CancellationToken.None);
                    await tx.CommitAsync();
                    _committed = true;
                }
            }
            catch
            {
                try { await tx.RollbackAsync(CancellationToken.None); }
                catch { /* 尽力回滚 */ }
            }
            finally
            {
                await tx.DisposeAsync();
                await conn.DisposeAsync();
            }
        }
    }

    /// <summary>内存行集的只读 DbDataReader（供 SqlBulkCopy 消费；仅实现其所需的成员）。</summary>
    private sealed class RowDataReader(IReadOnlyList<string> cols, List<object?[]> rows) : DbDataReader
    {
        private int _pos = -1;

        public override int FieldCount => cols.Count;
        public override bool HasRows => rows.Count > 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override object this[int i] => rows[_pos][i];
        public override object this[string name] => rows[_pos][GetOrdinal(name)];

        public override bool Read()
        {
            _pos++;
            return _pos < rows.Count;
        }
        public override string GetName(int ordinal) => cols[ordinal];
        public override int GetOrdinal(string name)
        {
            for (var i = 0; i < cols.Count; i++)
                if (string.Equals(cols[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new IndexOutOfRangeException(name);
        }
        public override object GetValue(int ordinal) => rows[_pos][ordinal] ?? DBNull.Value;
        public override bool IsDBNull(int ordinal) => rows[_pos][ordinal] is null or DBNull;
        public override int GetValues(object[] values)
        {
            var n = Math.Min(values.Length, cols.Count);
            for (var i = 0; i < n; i++) values[i] = rows[_pos][i] ?? DBNull.Value;
            return n;
        }
        public override DataTable? GetSchemaTable()
        {
            var t = new DataTable("SchemaTable");
            t.Columns.Add("ColumnName", typeof(string));
            t.Columns.Add("ColumnOrdinal", typeof(int));
            t.Columns.Add("ColumnSize", typeof(int));
            t.Columns.Add("DataType", typeof(Type));
            for (var i = 0; i < cols.Count; i++)
                t.Rows.Add(cols[i], i, 0, typeof(object));
            return t;
        }
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
        public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            var bytes = (byte[])GetValue(ordinal)!;
            var n = Math.Min(length, bytes.Length - dataOffset);
            Array.Copy(bytes, dataOffset, buffer!, bufferOffset, n);
            return n;
        }
        public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
        public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
        public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
        public override System.Collections.IEnumerator GetEnumerator() => rows.GetEnumerator();
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
        public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override Type GetFieldType(int ordinal) => GetValue(ordinal) switch
        {
            null or DBNull => typeof(object),
            var v => v.GetType(),
        };
        public override bool NextResult() => false;
    }
}
