using System.Data;
using System.Data.Common;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace Ark.Providers.SQLite;

public sealed class SqliteProvider : ProviderBase
{
    public SqliteProvider(ConnectionSpec spec) : base(spec) { }

    public override ArkDialect Dialect => ArkDialect.SQLite;

    public override DbCapabilities Capabilities { get; } =
        new(SupportsSchemas: false, SupportsMultipleDatabases: false, SupportsTruncate: false,
            SupportsDropColumn: true, SupportsAlterColumnType: false, SupportsIndexConcurrently: false,
            SupportsBulkCopy: true, SupportsConsistentSnapshot: true, ServerKind: "SQLite");

    public override IDdlGenerator Ddl { get; } = new SqliteDdlGenerator();

    protected override DbConnection CreateConnection(string? database)
    {
        var path = Spec.FilePath;
        if (string.IsNullOrWhiteSpace(path))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "SQLite 连接缺少文件路径");
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = Spec.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        return new SqliteConnection(b.ToString());
    }

    protected override IReadOnlySet<string> BuiltinFunctions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "current_timestamp", "current_date", "current_time", "coalesce", "ifnull", "date", "time", "datetime", "strftime",
        };

    public override async Task<string> TestAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 'SQLite ' || sqlite_version()";
        return (string?)(await cmd.ExecuteScalarAsync(ct)) ?? "SQLite";
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
            "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var list = new List<TableSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new TableSummary(reader.GetString(0), reader.GetString(1) == "view" ? "view" : "table", null));
        return list;
    }

    public override async Task<CompletionSchema> GetCompletionSchemaAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var tables = await ListTablesAsync(database, schema, ct);
        var conn = await GetConnAsync(null, ct);
        var list = new List<CompletionTable>(tables.Count);
        foreach (var t in tables)
        {
            var cols = new List<CompletionColumn>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_xinfo({Ddl.Quote(t.Name)})";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    cols.Add(new CompletionColumn(r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2)));
            }
            list.Add(new CompletionTable(t.Name, t.Kind, cols));
        }
        return new CompletionSchema(list);
    }

    public override Task<ExplainResponse> ExplainAsync(
        string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        // EXPLAIN QUERY PLAN 输出 id | parent | notused | detail，仅保留 detail 列
        return ReadPlanAsync(database, "EXPLAIN QUERY PLAN " + sql, sql, timeoutSeconds, ct,
            r => r.FieldCount >= 4 && !r.IsDBNull(3) ? r.GetString(3) : r.GetValue(0)?.ToString() ?? "");
    }

    public override async Task<CanonicalTable> GetTableAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        var conn = await GetConnAsync(null, ct);

        var isView = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT type FROM sqlite_master WHERE name = @n";
            var p = cmd.CreateParameter();
            p.ParameterName = "@n";
            p.Value = table;
            cmd.Parameters.Add(p);
            var t = await cmd.ExecuteScalarAsync(ct) as string
                    ?? throw new ArkException(ArkErrorCodes.TableNotFound, $"表 {table} 不存在", 404);
            isView = t == "view";
        }

        // 列（table_xinfo 含生成列 hidden 标记：2=VIRTUAL 生成，3=STORED 生成）
        var columns = new List<CanonicalColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_xinfo({Ddl.Quote(table)})";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(1);
                var declared = r.IsDBNull(2) ? "" : r.GetString(2);
                var notNull = r.GetInt32(3) == 1;
                var dflt = r.IsDBNull(4) ? null : r.GetString(4);
                var pk = r.GetInt32(5);
                var hidden = r.GetInt32(6);
                var kind = FunctionTranslator.Recognize(ArkDialect.SQLite, dflt);
                columns.Add(new CanonicalColumn
                {
                    Name = name,
                    Type = TypeMapper.ToCanonical(ArkDialect.SQLite, declared, null, null, null),
                    Nullable = !notNull && pk == 0,
                    DefaultValueSql = dflt,
                    DefaultKind = kind,
                    IsPrimaryKey = pk > 0,
                    IsGenerated = hidden is 2 or 3,
                    GeneratedExpression = hidden is 2 or 3 ? null : null,
                });
            }
        }

        // 主键（table_info 的 pk 顺序）
        var pkColumns = columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();

        // 行 id 别名检测：单一 INTEGER 主键 → rowid 别名（等价自增语义）
        if (pkColumns.Count == 1)
        {
            var pk = columns.First(c => c.IsPrimaryKey);
            if (pk.Type.Id == CanonicalTypeId.Int64)
            {
                columns = columns.Select(c => c with { IsAutoIncrement = c.Name == pk.Name }).ToList();
            }
        }

        // 索引
        var indexes = new List<CanonicalIndex>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list({Ddl.Quote(table)})";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<(string Name, bool Unique, string Origin)>();
            while (await r.ReadAsync(ct))
                rows.Add((r.GetString(1), r.GetInt32(2) == 1, r.IsDBNull(3) ? "c" : r.GetString(3)));
            foreach (var (name, unique, origin) in rows)
            {
                var cols = new List<string>();
                await using var cmd2 = conn.CreateCommand();
                cmd2.CommandText = $"PRAGMA index_info({Ddl.Quote(name)})";
                await using var r2 = await cmd2.ExecuteReaderAsync(ct);
                while (await r2.ReadAsync(ct))
                    if (!r2.IsDBNull(2)) cols.Add(r2.GetString(2));
                // pk / unique 约束的内部自动索引（sqlite_autoindex_*）：主键已由 PK 表达；
                // UNIQUE 约束转换为显式命名的唯一索引同步到目标端（SQLite 内部名不可复用）。
                if (origin is "pk") continue;
                var indexName = name.StartsWith("sqlite_autoindex_", StringComparison.Ordinal)
                    ? $"uq_{table}_{string.Join("_", cols)}"
                    : name;
                indexes.Add(new CanonicalIndex
                {
                    Name = indexName,
                    Columns = cols,
                    IsUnique = unique || origin is "u",
                    IsPrimaryKey = false,
                });
            }
        }

        // 外键
        var fks = new List<CanonicalForeignKey>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA foreign_key_list({Ddl.Quote(table)})";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byId = new SortedDictionary<int, CanonicalForeignKey>();
            var colMap = new SortedDictionary<int, List<string>>();
            var refColMap = new SortedDictionary<int, List<string?>>();
            while (await r.ReadAsync(ct))
            {
                var id = r.GetInt32(0);
                var fkTable = r.GetString(2);
                var from = r.GetString(3);
                var to = r.IsDBNull(4) ? null : r.GetString(4);
                var onUpdate = r.IsDBNull(5) ? null : r.GetString(5);
                var onDelete = r.IsDBNull(6) ? null : r.GetString(6);
                if (!byId.TryGetValue(id, out var fk))
                {
                    fk = new CanonicalForeignKey
                    {
                        Name = $"fk_{table}_{id}",
                        Columns = [],
                        ReferencedTable = fkTable,
                        ReferencedColumns = [],
                        OnDelete = onDelete,
                        OnUpdate = onUpdate,
                    };
                    byId[id] = fk;
                    colMap[id] = [];
                    refColMap[id] = [];
                }
                colMap[id].Add(from);
                refColMap[id].Add(to ?? "rowid");
            }
            foreach (var (id, fk) in byId)
            {
                var fk2 = fk with { Columns = colMap[id], ReferencedColumns = refColMap[id].ToList()! };
                fks.Add(fk2);
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
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = @n";
        var p = cmd.CreateParameter();
        p.ParameterName = "@n";
        p.Value = table;
        cmd.Parameters.Add(p);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public override Task<IReadOnlyList<ExtensionDependency>> CheckFunctionsAsync(
        string database, IReadOnlyList<string> functions, CancellationToken ct = default)
    {
        IReadOnlyList<ExtensionDependency> result = functions
            .Select(f => new ExtensionDependency
            {
                Name = f,
                InstallSql = "",
                Present = BuiltinFunctions.Contains(f),
                Note = BuiltinFunctions.Contains(f)
                    ? null
                    : $"SQLite 无内置函数 {f}，该默认值无法自动翻译",
            })
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
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name = @n AND type = 'view'";
        var p = cmd.CreateParameter();
        p.ParameterName = "@n";
        p.Value = view;
        cmd.Parameters.Add(p);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public override async Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default)
    {
        var conn = CreateConnection(database);
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "BEGIN";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new SqliteSnapshotSession(conn);
    }

    private sealed class SqliteSnapshotSession(DbConnection conn) : ISnapshotSession
    {
        public DbConnection Connection => conn;
        public string Description => "BEGIN（deferred 快照）";

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

    public override IReadOnlyList<string> BulkLoadPragmas => ["PRAGMA synchronous=OFF", "PRAGMA foreign_keys=OFF"];

    public override async Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default)
    {
        var conn = await OpenConnectionAsync(table.Database, ct);
        return new SqliteBatchWriter(conn, Ddl.TableName(table), columns);
    }

    /// <summary>SQLite 批量写入通道：单事务 + 预编译语句 + 多值 INSERT 分批（比逐批自建事务快）。</summary>
    private sealed class SqliteBatchWriter(DbConnection conn, string tableName, IReadOnlyList<string> columns) : IBulkWriter
    {
        private const int BatchRows = 500;
        private readonly List<object?[]> _buffer = [];
        private SqliteTransaction? _tx;

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
            _tx ??= (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            var colSql = string.Join(", ", columns.Select(c => $"\"{c}\""));
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
                    p.ParameterName = $"@b{pi++}";
                    p.Value = CellValue.ToParam(row[c], ArkDialect.SQLite);
                    cmd.Parameters.Add(p);
                    ps.Add(p.ParameterName);
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
}
