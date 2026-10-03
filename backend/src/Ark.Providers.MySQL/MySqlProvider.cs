using System.Data.Common;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using MySqlConnector;

namespace Ark.Providers.MySQL;

public sealed class MySqlProvider : ProviderBase
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    { "information_schema", "mysql", "performance_schema", "sys" };

    public MySqlProvider(ConnectionSpec spec) : base(spec) { }

    public override ArkDialect Dialect => ArkDialect.MySQL;

    public override DbCapabilities Capabilities { get; } =
        new(SupportsSchemas: false, SupportsMultipleDatabases: true, SupportsTruncate: true,
            SupportsDropColumn: true, SupportsAlterColumnType: true, SupportsIndexConcurrently: false,
            SupportsBulkCopy: true, SupportsConsistentSnapshot: true, ServerKind: "MySQL");

    public override IDdlGenerator Ddl { get; } = new MySqlDdlGenerator();

    protected override DbConnection CreateConnection(string? database)
    {
        if (string.IsNullOrWhiteSpace(Spec.Host))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "MySQL 连接缺少主机地址");
        var b = new MySqlConnectionStringBuilder
        {
            Server = Spec.Host,
            Port = (uint)(Spec.Port ?? 3306),
            UserID = Spec.Username ?? "root",
            Password = Spec.Password ?? "",
            Database = string.IsNullOrEmpty(database) ? (Spec.Database ?? "") : database,
            CharacterSet = "utf8mb4",
            AllowPublicKeyRetrieval = true,
            AllowLoadLocalInfile = true, // MySqlBulkCopy 走 LOAD DATA LOCAL 协议；服务端未放行时自动降级
            SslMode = MySqlSslMode.Preferred,
            DefaultCommandTimeout = 60,
        };
        return new MySqlConnection(b.ConnectionString);
    }

    protected override IReadOnlySet<string> BuiltinFunctions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "uuid", "now", "current_timestamp", "current_date", "current_time", "curdate", "curtime", "coalesce", "ifnull", "concat" };

    public override async Task<string> TestAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT VERSION()";
        return "MySQL " + (await cmd.ExecuteScalarAsync(ct));
    }

    public override async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SHOW DATABASES";
        var list = new List<DatabaseInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!SystemDatabases.Contains(name)) list.Add(new DatabaseInfo(name));
        }
        return list;
    }

    public override Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(string database, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SchemaInfo>>([new SchemaInfo(database)]);

    public override async Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TABLE_NAME, TABLE_TYPE, TABLE_ROWS FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = @db AND TABLE_TYPE IN ('BASE TABLE','VIEW') ORDER BY TABLE_NAME";
        cmd.Parameters.Add(new MySqlParameter("@db", database));
        var list = new List<TableSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long? rows = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            list.Add(new TableSummary(reader.GetString(0), reader.GetString(1) == "VIEW" ? "view" : "table",
                rows < 0 ? null : rows));
        }
        return list;
    }

    public override async Task<CompletionSchema> GetCompletionSchemaAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT t.TABLE_NAME, t.TABLE_TYPE, c.COLUMN_NAME, c.DATA_TYPE " +
            "FROM information_schema.TABLES t " +
            "LEFT JOIN information_schema.COLUMNS c ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME " +
            "WHERE t.TABLE_SCHEMA = @db AND t.TABLE_TYPE IN ('BASE TABLE','VIEW') " +
            "ORDER BY t.TABLE_NAME, c.ORDINAL_POSITION";
        cmd.Parameters.Add(new MySqlParameter("@db", database));

        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var colsByName = new Dictionary<string, List<CompletionColumn>>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            if (!colsByName.TryGetValue(name, out var cols))
            {
                cols = [];
                colsByName[name] = cols;
                kinds[name] = reader.GetString(1) == "VIEW" ? "view" : "table";
            }
            cols.Add(new CompletionColumn(reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        var tables = colsByName
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CompletionTable(kv.Key, kinds[kv.Key], kv.Value))
            .ToList();
        return new CompletionSchema(tables);
    }

    public override async Task<ExplainResponse> ExplainAsync(
        string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        var isTree = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT VERSION()";
            var v = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "";
            isTree = Version.TryParse(v.Split('-')[0].Trim(), out var ver) && ver.Major >= 8;
        }
        // MySQL 8+ 用 FORMAT=TREE；旧版回退传统表格输出
        var prefix = isTree ? "EXPLAIN FORMAT=TREE " : "EXPLAIN ";
        return await ReadPlanAsync(database, prefix + sql, sql, timeoutSeconds, ct);
    }

    public override async Task<CanonicalTable> GetTableAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        Identifier.EnsureValid(database, "库名");
        var conn = await GetConnAsync(null, ct);

        var isView = false;
        var engine = (string?)"InnoDB";
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT TABLE_TYPE, ENGINE FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@t";
            cmd.Parameters.Add(new MySqlParameter("@db", database));
            cmd.Parameters.Add(new MySqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw new ArkException(ArkErrorCodes.TableNotFound, $"表 {database}.{table} 不存在", 404);
            isView = r.GetString(0) == "VIEW";
            engine = r.IsDBNull(1) ? null : r.GetString(1);
        }

        // 列
        var columns = new List<CanonicalColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, COLUMN_KEY, EXTRA, COLUMN_COMMENT, GENERATION_EXPRESSION " +
                "FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@t ORDER BY ORDINAL_POSITION";
            cmd.Parameters.Add(new MySqlParameter("@db", database));
            cmd.Parameters.Add(new MySqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var colType = r.GetString(1);
                var nullable = r.GetString(2) == "YES";
                var dflt = r.IsDBNull(3) ? null : r.GetString(3);
                var key = r.GetString(4);
                var extra = r.IsDBNull(5) ? "" : r.GetString(5);
                var comment = r.IsDBNull(6) ? null : r.GetString(6);
                var genExpr = r.IsDBNull(7) ? "" : r.GetString(7);
                // DEFAULT_GENERATED 是“表达式默认值”（8.0+），不是生成列；真正的生成列是 VIRTUAL/STORED GENERATED
                var isGenerated = extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase)
                                  && !extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase);
                var isAuto = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
                var dfltExpr = isGenerated ? null : NormalizeMyDefault(dflt, extra);
                columns.Add(new CanonicalColumn
                {
                    Name = name,
                    Type = TypeMapper.ToCanonical(ArkDialect.MySQL, colType, null, null, null),
                    Nullable = nullable,
                    DefaultValueSql = dfltExpr,
                    DefaultKind = isGenerated || isAuto
                        ? ValueExprKind.None
                        : FunctionTranslator.Recognize(ArkDialect.MySQL, dfltExpr),
                    IsAutoIncrement = isAuto,
                    IsPrimaryKey = key == "PRI",
                    Comment = comment,
                    IsGenerated = isGenerated,
                    GeneratedExpression = isGenerated && genExpr.Length > 0 ? genExpr : null,
                });
            }
        }

        var pkColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        // 索引
        var indexes = new List<CanonicalIndex>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME, INDEX_TYPE FROM information_schema.STATISTICS " +
                "WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@t AND INDEX_NAME <> 'PRIMARY' ORDER BY INDEX_NAME, SEQ_IN_INDEX";
            cmd.Parameters.Add(new MySqlParameter("@db", database));
            cmd.Parameters.Add(new MySqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byName = new Dictionary<string, (bool Unique, string? Method, List<string> Cols)>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var unique = r.GetInt32(1) == 0;
                var col = r.GetString(2);
                var method = r.IsDBNull(3) ? null : r.GetString(3);
                if (!byName.TryGetValue(name, out var e))
                {
                    e = (unique, method, []);
                    byName[name] = e;
                }
                e.Cols.Add(col);
            }
            indexes.AddRange(byName.Select(kv => new CanonicalIndex
            {
                Name = kv.Key,
                Columns = kv.Value.Cols,
                IsUnique = kv.Value.Unique,
                IsPrimaryKey = false,
                Method = kv.Value.Method is "BTREE" ? null : kv.Value.Method,
            }));
        }

        // 外键
        var fks = new List<CanonicalForeignKey>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT kcu.CONSTRAINT_NAME, kcu.COLUMN_NAME, kcu.REFERENCED_TABLE_NAME, kcu.REFERENCED_COLUMN_NAME, rc.UPDATE_RULE, rc.DELETE_RULE " +
                "FROM information_schema.KEY_COLUMN_USAGE kcu " +
                "JOIN information_schema.REFERENTIAL_CONSTRAINTS rc " +
                "  ON rc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND rc.CONSTRAINT_SCHEMA = kcu.CONSTRAINT_SCHEMA " +
                "WHERE kcu.TABLE_SCHEMA=@db AND kcu.TABLE_NAME=@t AND kcu.REFERENCED_TABLE_NAME IS NOT NULL " +
                "ORDER BY kcu.CONSTRAINT_NAME, kcu.ORDINAL_POSITION";
            cmd.Parameters.Add(new MySqlParameter("@db", database));
            cmd.Parameters.Add(new MySqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byName = new Dictionary<string, (string RefTable, string? OnUpdate, string? OnDelete, List<string> Cols, List<string> RefCols)>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var col = r.GetString(1);
                var refTable = r.GetString(2);
                var refCol = r.GetString(3);
                var onUpdate = r.IsDBNull(4) ? null : r.GetString(4);
                var onDelete = r.IsDBNull(5) ? null : r.GetString(5);
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

        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = engine,
            ["database"] = database,
        };

        return new CanonicalTable
        {
            Name = table,
            Columns = columns,
            PrimaryKeyColumns = pkColumns,
            Indexes = indexes,
            ForeignKeys = fks,
            Options = options,
            IsView = isView,
        };
    }

    /// <summary>MySQL 的 NULL 默认值与"无默认"无法区分（都是 NULL），CURRENT_TIMESTAMP 字面量保留。</summary>
    private static string? NormalizeMyDefault(string? dflt, string extra)
    {
        if (dflt is null) return null;
        // extra 含 DEFAULT_GENERATED 表示表达式默认值（8.0+），原样保留
        return dflt;
    }

    public override async Task<string?> GetCreateTableSqlAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        Identifier.EnsureValid(database, "库名");
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SHOW CREATE TABLE {Ddl.Quote(database)}.{Ddl.Quote(table)}";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return reader.GetString(1);
    }

    public override async Task<long> EstimateRowsAsync(TableRef table, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@t";
        cmd.Parameters.Add(new MySqlParameter("@db", table.Database));
        cmd.Parameters.Add(new MySqlParameter("@t", table.Table));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v);
    }

    // ------------------------------------------------ 同步运行时 ------------------------------------------------

    public override async Task<string?> GetViewDefinitionAsync(
        string database, string? schema, string view, CancellationToken ct = default)
    {
        Identifier.EnsureValid(view, "视图名");
        Identifier.EnsureValid(database, "库名");
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SHOW CREATE VIEW {Ddl.Quote(database)}.{Ddl.Quote(view)}";
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) && !r.IsDBNull(1) ? r.GetString(1) : null;
        }
        catch (MySqlException)
        {
            return null;
        }
    }

    public override async Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default)
    {
        var conn = CreateConnection(database);
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            // InnoDB 一致快照；MyISAM 表不参与 MVCC（计划期已有警告）
            cmd.CommandText = "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ; " +
                              "START TRANSACTION WITH CONSISTENT SNAPSHOT";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new MySqlSnapshotSession(conn);
    }

    private sealed class MySqlSnapshotSession(DbConnection conn) : ISnapshotSession
    {
        public DbConnection Connection => conn;
        public string Description => "REPEATABLE READ + CONSISTENT SNAPSHOT";

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "ROLLBACK";
                await cmd.ExecuteNonQueryAsync();
            }
            catch { /* 尽力回滚 */ }
            await conn.DisposeAsync();
        }
    }

    public override IReadOnlyList<string> BulkLoadPragmas =>
    [
        "SET autocommit=0",
        "SET FOREIGN_KEY_CHECKS=0",
        "SET unique_checks=0",
    ];

    public override async Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default)
    {
        var conn = (MySqlConnection)await OpenConnectionAsync(table.Database, ct);
        return new MySqlBulkWriter(conn, Ddl.TableName(table), columns);
    }

    /// <summary>
    /// MySqlBulkCopy（LOAD DATA LOCAL 协议）写入器：按批缓冲提交；
    /// 服务端未放行 local_infile 等失败时自动降级为多行 INSERT 并在日志中说明。
    /// </summary>
    private sealed class MySqlBulkWriter(MySqlConnection conn, string tableName, IReadOnlyList<string> columns) : IBulkWriter
    {
        private const int BatchRows = 2000;
        private readonly List<object?[]> _buffer = [];
        private bool _degraded;
        private bool _failed;

        public long RowsWritten { get; private set; }

        public string Channel => _degraded ? "批量 INSERT（LOAD DATA 不可用已降级）" : "LOAD DATA LOCAL";

        public async Task WriteAsync(object?[] values, CancellationToken ct = default)
        {
            _buffer.Add(values);
            if (_buffer.Count >= BatchRows) await FlushAsync(ct);
        }

        public async Task CompleteAsync(CancellationToken ct = default) => await FlushAsync(ct);

        private async Task FlushAsync(CancellationToken ct)
        {
            if (_buffer.Count == 0) return;
            if (!_degraded)
            {
                try
                {
                    var bulk = new MySqlBulkCopy(conn)
                    {
                        DestinationTableName = tableName,
                        BulkCopyTimeout = 0,
                        ColumnMappings = { },
                    };
                    for (var i = 0; i < columns.Count; i++)
                        bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, columns[i]));
                    await bulk.WriteToServerAsync(new RowDataReader(columns, _buffer), ct);
                    RowsWritten += _buffer.Count;
                    _buffer.Clear();
                    return;
                }
                catch (Exception ex) when (!_failed)
                {
                    _failed = true;
                    _degraded = true; // 首次失败后整体降级，缓冲数据走 INSERT 重写
                    _ = ex;
                }
            }
            // 降级：MySqlConnector 批量 INSERT（无冲突语义需求 —— SkipExisting 不走此通道）
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                foreach (var chunk in _buffer.Chunk(500))
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = (MySqlTransaction)tx;
                    var groups = new List<string>();
                    var pi = 0;
                    foreach (var row in chunk)
                    {
                        var ps = new List<string>();
                        for (var c = 0; c < columns.Count; c++)
                        {
                            var p = cmd.CreateParameter();
                            p.ParameterName = $"@b{pi++}";
                            p.Value = CellValue.ToParam(row[c], ArkDialect.MySQL);
                            cmd.Parameters.Add(p);
                            ps.Add(p.ParameterName);
                        }
                        groups.Add("(" + string.Join(", ", ps) + ")");
                    }
                    cmd.CommandText =
                        $"INSERT INTO {tableName} ({string.Join(", ", columns.Select(c => $"`{c}`"))}) VALUES {string.Join(", ", groups)}";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                RowsWritten += _buffer.Count;
                _buffer.Clear();
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_buffer.Count > 0) await FlushAsync(CancellationToken.None);
            await Task.CompletedTask;
        }
    }

    /// <summary>内存行集的只读 DbDataReader（供 MySqlBulkCopy 消费；仅实现其所需的成员）。</summary>
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
