using System.Data.Common;
using System.Globalization;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using global::ClickHouse.Client.ADO;
using global::ClickHouse.Client.Copy;

namespace Ark.Providers.ClickHouse;

/// <summary>ClickHouse 提供者：元数据取自 system 库（system.databases/tables/columns），HTTP 协议，无事务语义。</summary>
public sealed class ClickHouseProvider : ProviderBase
{
    public ClickHouseProvider(ConnectionSpec spec) : base(spec) { }

    public override ArkDialect Dialect => ArkDialect.ClickHouse;

    public override DbCapabilities Capabilities { get; } =
        new(SupportsSchemas: false, SupportsMultipleDatabases: true, SupportsTruncate: true,
            SupportsDropColumn: true, SupportsAlterColumnType: true, SupportsIndexConcurrently: false,
            SupportsBulkCopy: true, SupportsConsistentSnapshot: false, ServerKind: "ClickHouse");

    public override IDdlGenerator Ddl { get; } = new ClickHouseDdlGenerator();

    protected override IReadOnlySet<string> BuiltinFunctions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "now", "today", "yesterday", "coalesce", "ifnull", "concat", "generateuuidv4", "uuid" };

    protected override DbConnection CreateConnection(string? database)
    {
        if (string.IsNullOrWhiteSpace(Spec.Host))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "ClickHouse 连接缺少主机地址");
        var b = new ClickHouseConnectionStringBuilder
        {
            Host = Spec.Host,
            Port = (ushort)(Spec.Port ?? 8123),
            Database = string.IsNullOrEmpty(database) ? (Spec.Database ?? "default") : database,
            Username = Spec.Username ?? "default",
            Password = Spec.Password ?? "",
        };
        return new ClickHouseConnection(b.ConnectionString);
    }

    public override async Task<string> TestAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version()";
        return (string?)(await cmd.ExecuteScalarAsync(ct)) ?? "ClickHouse";
    }

    public override async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT name FROM system.databases " +
            "WHERE name NOT IN ('system','INFORMATION_SCHEMA','information_schema') ORDER BY name";
        var list = new List<DatabaseInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new DatabaseInfo(reader.GetString(0)));
        return list;
    }

    public override Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(string database, CancellationToken ct = default) =>
        // ClickHouse 无独立 schema 层，树形按库名展开
        Task.FromResult<IReadOnlyList<SchemaInfo>>([new SchemaInfo("default")]);

    public override async Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, engine, total_rows FROM system.tables WHERE database = @db ORDER BY name";
        cmd.Parameters.Add(Param(cmd, "db", database));
        var list = new List<TableSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = IsViewEngine(reader.GetString(1)) ? "view" : "table";
            long? rows = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            list.Add(new TableSummary(reader.GetString(0), kind, rows));
        }
        return list;
    }

    public override async Task<CanonicalTable> GetTableAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);

        // 表存在性 + 引擎 + 排序键（主键来源）
        var engine = "";
        string? sortingKey = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT engine, sorting_key FROM system.tables WHERE database = @db AND name = @tb";
            cmd.Parameters.Add(Param(cmd, "db", database));
            cmd.Parameters.Add(Param(cmd, "tb", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                throw new ArkException(ArkErrorCodes.TableNotFound, $"表 {database}.{table} 不存在", 404);
            engine = r.GetString(0);
            sortingKey = r.IsDBNull(1) ? null : r.GetString(1);
        }
        var isView = IsViewEngine(engine);
        var pkColumns = (IReadOnlyList<string>)ParseSortingKey(sortingKey);

        // 列（Nullable/LowCardinality 解包后交 TypeMapper）
        var columns = new List<CanonicalColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name, type, default_kind, default_expression FROM system.columns " +
                "WHERE database = @db AND table = @tb ORDER BY position";
            cmd.Parameters.Add(Param(cmd, "db", database));
            cmd.Parameters.Add(Param(cmd, "tb", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var (innerType, nullable) = UnwrapChType(r.GetString(1));
                var defaultKindRaw = r.IsDBNull(2) ? "" : r.GetString(2);
                var defaultExpr = r.IsDBNull(3) ? null : r.GetString(3);
                // 仅 DEFAULT 显式默认值可跨库表达；MATERIALIZED/ALIAS/EPHEMERAL 忽略
                var hasDefault = defaultKindRaw == "DEFAULT" && !string.IsNullOrWhiteSpace(defaultExpr);
                columns.Add(new CanonicalColumn
                {
                    Name = name,
                    Type = TypeMapper.ToCanonical(ArkDialect.ClickHouse, innerType, null, null, null),
                    Nullable = nullable,
                    DefaultValueSql = hasDefault ? defaultExpr : null,
                    DefaultKind = hasDefault ? FunctionTranslator.Recognize(ArkDialect.ClickHouse, defaultExpr) : ValueExprKind.None,
                    IsPrimaryKey = pkColumns.Contains(name),
                });
            }
        }

        // 外键：ClickHouse 无 → 空；二级索引：system.tables 无信息（SKIP INDEX 暂不支持）→ 空
        return new CanonicalTable
        {
            Name = table,
            Columns = columns,
            PrimaryKeyColumns = pkColumns,
            Indexes = [],
            ForeignKeys = [],
            IsView = isView,
        };
    }

    public override async Task<string?> GetCreateTableSqlAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT create_table_query FROM system.tables WHERE database = @db AND name = @tb";
        cmd.Parameters.Add(Param(cmd, "db", database));
        cmd.Parameters.Add(Param(cmd, "tb", table));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public override async Task<long> EstimateRowsAsync(TableRef table, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(table.Database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT total_rows FROM system.tables WHERE database = @db AND name = @tb";
        cmd.Parameters.Add(Param(cmd, "db", table.Database));
        cmd.Parameters.Add(Param(cmd, "tb", table.Table));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    public override Task<ExplainResponse> ExplainAsync(
        string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default) =>
        // EXPLAIN 每行一个计划文本行（单列 explain）
        ReadPlanAsync(database, "EXPLAIN " + sql, sql, timeoutSeconds, ct);

    // ------------------------------------------------ 同步运行时 ------------------------------------------------

    public override Task<string?> GetViewDefinitionAsync(
        string database, string? schema, string view, CancellationToken ct = default) =>
        GetCreateTableSqlAsync(database, schema, view, ct);

    public override Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default) =>
        throw new NotSupportedException("ClickHouse 无事务，无法提供一致快照，将按普通读执行");

    public override IReadOnlyList<string> BulkLoadPragmas => [];

    public override async Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default)
    {
        var conn = await OpenConnectionAsync(table.Database, ct);
        var bulk = new ClickHouseBulkCopy((ClickHouseConnection)conn)
        {
            DestinationTableName = Ddl.TableName(table),
            ColumnNames = columns.ToList(),
            BatchSize = 50_000,
            MaxDegreeOfParallelism = 2,
        };
        await bulk.InitAsync();
        return new ClickHouseBulkWriter(bulk);
    }

    /// <summary>ClickHouseBulkCopy (RowBinary) 写入通道：缓冲到批次后整批 WriteToServerAsync。</summary>
    private sealed class ClickHouseBulkWriter(ClickHouseBulkCopy bulk) : IBulkWriter
    {
        private const int BatchRows = 10_000;
        private readonly List<object?[]> _buffer = [];
        private bool _completed;

        public long RowsWritten { get; private set; }

        public string Channel => "ClickHouseBulkCopy (RowBinary)";

        public async Task WriteAsync(object?[] values, CancellationToken ct = default)
        {
            _buffer.Add(values);
            if (_buffer.Count >= BatchRows) await FlushAsync(ct);
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            await FlushAsync(ct);
            _completed = true;
        }

        private async Task FlushAsync(CancellationToken ct)
        {
            if (_buffer.Count == 0) return;
            await bulk.WriteToServerAsync(_buffer.ToArray(), ct);
            RowsWritten += _buffer.Count;
            _buffer.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_completed) await FlushAsync(CancellationToken.None);
            }
            catch { /* 尽力写入 */ }
            bulk.Dispose();
        }
    }

    // ------------------------------------------------ 变更集（无事务语义） ------------------------------------------------

    /// <summary>
    /// ClickHouse 无事务：不开启事务，逐条执行 Insert/Update/Delete。
    /// 中途失败已写入的行不回滚，直接抛异常由上层报告。
    /// </summary>
    public override async Task<ApplyChangesResponse> ApplyChangesAsync(
        TableRef table, CanonicalTable meta, RowChangeSet changes, CancellationToken ct = default)
    {
        if (Spec.ReadOnly)
            throw new ArkException(ArkErrorCodes.ReadOnlyMode, "连接处于只读模式，无法提交修改", 403);
        if (meta.PrimaryKeyColumns.Count == 0 && (changes.Updates.Count > 0 || changes.Deletes.Count > 0))
            throw new ArkException(ArkErrorCodes.RowChangeFailed, "该表没有主键，无法定位行进行更新/删除");

        var conn = await GetConnAsync(table.Database, ct);
        int inserted = 0, updated = 0, deleted = 0;
        foreach (var ins in changes.Inserts) inserted += await ExecuteInsert(conn, table, meta, ins, ct);
        foreach (var upd in changes.Updates) updated += await ExecuteUpdate(conn, table, meta, upd, ct);
        foreach (var del in changes.Deletes) deleted += await ExecuteDelete(conn, table, meta, del, ct);
        return new ApplyChangesResponse(inserted, updated, deleted);
    }

    private async Task<int> ExecuteInsert(DbConnection conn, TableRef table, CanonicalTable meta, RowInsert ins, CancellationToken ct)
    {
        var cols = ins.Values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        if (cols.Count == 0) return 0;
        foreach (var c in cols) Identifier.EnsureValid(c, "列名");
        await using var cmd = conn.CreateCommand();
        var names = string.Join(", ", cols.Select(Ddl.Quote));
        var ps = new List<string>();
        foreach (var c in cols)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "v" + ps.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(ins.Values[c], TypeOf(meta, c)), Dialect);
            cmd.Parameters.Add(p);
            ps.Add("@" + p.ParameterName);
        }
        cmd.CommandText = $"INSERT INTO {Ddl.TableName(table)} ({names}) VALUES ({string.Join(", ", ps)})";
        await cmd.ExecuteNonQueryAsync(ct);
        // CH 的 INSERT 不回报受影响行数（恒 0）；INSERT 本身同步可靠，成功即计 1
        return 1;
    }

    private async Task<int> ExecuteUpdate(DbConnection conn, TableRef table, CanonicalTable meta, RowUpdate upd, CancellationToken ct)
    {
        if (upd.Values.Count == 0) return 0;
        await using var cmd = conn.CreateCommand();
        var sets = new List<string>();
        foreach (var (col, v) in upd.Values)
        {
            if (meta.PrimaryKeyColumns.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;
            Identifier.EnsureValid(col, "列名");
            var p = cmd.CreateParameter();
            p.ParameterName = "s" + sets.Count;
            p.Value = CellValue.ToParam(CellValue.FromJson(v, TypeOf(meta, col)), Dialect);
            cmd.Parameters.Add(p);
            sets.Add($"{Ddl.Quote(col)} = @{p.ParameterName}");
        }
        var where = BuildKeyWhere(cmd, meta, upd.Key);
        if (sets.Count == 0 || where.Length == 0) return 0;
        cmd.CommandText = $"ALTER TABLE {Ddl.TableName(table)} UPDATE {string.Join(", ", sets)} WHERE {where}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ExecuteDelete(DbConnection conn, TableRef table, CanonicalTable meta, RowDelete del, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var where = BuildKeyWhere(cmd, meta, del.Key);
        if (where.Length == 0) return 0;
        cmd.CommandText = $"ALTER TABLE {Ddl.TableName(table)} DELETE WHERE {where}";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>主键定位 WHERE 片段（ClickHouse.Client 要求参数名不带 @、SQL 用 @name）。</summary>
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
            parts.Add($"{Ddl.Quote(col)} = @{p.ParameterName}");
        }
        return string.Join(" AND ", parts);
    }

    private static CanonicalType TypeOf(CanonicalTable meta, string column) =>
        meta.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.Type
        ?? CanonicalType.Str;

    // ------------------------------------------------ 工具 ------------------------------------------------

    private static DbParameter Param(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name; // ClickHouse.Client 按 "@" + name 匹配 SQL 占位符，name 不带 @
        p.Value = value;
        return p;
    }

    private static bool IsViewEngine(string engine) =>
        engine is "View" or "MaterializedView" or "LiveView" or "WindowView";

    /// <summary>解包外层 Nullable(...) / LowCardinality(...)：Nullable 决定可空性，类型串继续下传。</summary>
    internal static (string InnerType, bool Nullable) UnwrapChType(string type)
    {
        var t = type.Trim();
        var nullable = false;
        while (TryUnwrapOne(ref t, ref nullable)) { }
        return (t, nullable);
    }

    private static bool TryUnwrapOne(ref string t, ref bool nullable)
    {
        foreach (var wrapper in new[] { "Nullable", "LowCardinality" })
        {
            var prefix = wrapper + "(";
            if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !t.EndsWith(')')) continue;
            var inner = t[prefix.Length..^1].Trim();
            // 括号配对校验：首括号必须与末括号配对（避免 Map(String, Nullable(Int64)) 的内部嵌套被误剥）
            var depth = 0;
            var ok = true;
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '(') depth++;
                else if (inner[i] == ')') { depth--; if (depth < 0) { ok = false; break; } }
            }
            if (!ok || depth != 0) continue;
            if (wrapper == "Nullable") nullable = true;
            t = inner;
            return true;
        }
        return false;
    }

    /// <summary>解析 system.tables.sorting_key（如 "id"、"(a, b)"、"cityHash64(a, b)"）；tuple() → 无主键。</summary>
    internal static IReadOnlyList<string> ParseSortingKey(string? sortingKey)
    {
        if (string.IsNullOrWhiteSpace(sortingKey)) return [];
        var s = StripOuter(sortingKey.Trim());
        if (s.Length == 0 || s.Equals("tuple()", StringComparison.OrdinalIgnoreCase)) return [];
        // 去除可能的前缀函数 cityHash64(a, b)
        if (!s.StartsWith('(') && RegexFunctionCall.IsMatch(s)) s = StripOuter(s);
        var cols = FunctionTranslator.SplitTopLevelArgs(s)
            .Select(c => c.Trim().Trim('`', '"'))
            .Where(c => c.Length > 0)
            .ToList();
        return cols;
    }

    private static readonly System.Text.RegularExpressions.Regex RegexFunctionCall =
        new(@"^[A-Za-z_][A-Za-z0-9_]*\(.*\)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>剥离配对的外层括号（避免 (a)+(b) 被误剥）。</summary>
    private static string StripOuter(string s)
    {
        var r = s.Trim();
        while (r.Length >= 2 && r.StartsWith('(') && r.EndsWith(')'))
        {
            var inner = r[1..^1].Trim();
            var depth = 0;
            var ok = true;
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '(') depth++;
                else if (inner[i] == ')') { depth--; if (depth < 0) { ok = false; break; } }
            }
            if (!ok || depth != 0) break;
            r = inner;
        }
        return r;
    }
}
