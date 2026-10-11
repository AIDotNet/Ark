using System.Data;
using System.Data.Common;
using System.Globalization;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using Dm;

namespace Ark.Providers.DM8;

/// <summary>
/// 达梦 DM8 Provider（DM.DmProvider 官方驱动）。
/// DM 单库多模式：Database 无效，Owner=schema；系统视图兼容 Oracle（ALL_TAB_COLUMNS 等）。
/// </summary>
public sealed class Dm8Provider : ProviderBase
{
    public Dm8Provider(ConnectionSpec spec) : base(spec) { }

    public override ArkDialect Dialect => ArkDialect.DM8;

    public override DbCapabilities Capabilities { get; } =
        new(SupportsSchemas: true, SupportsMultipleDatabases: false, SupportsTruncate: true,
            SupportsDropColumn: true, SupportsAlterColumnType: true, SupportsIndexConcurrently: false,
            SupportsBulkCopy: true, SupportsConsistentSnapshot: true, ServerKind: "DM8");

    public override IDdlGenerator Ddl { get; } = new Dm8DdlGenerator();

    protected override DbConnection CreateConnection(string? database)
    {
        if (string.IsNullOrWhiteSpace(Spec.Host))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "达梦连接缺少主机地址");
        // Database 参数对 DM 无效（单库多模式），忽略 database
        var b = new DmConnectionStringBuilder
        {
            Server = Spec.Host,
            Port = Spec.Port ?? 5236,
            User = Spec.Username ?? "SYSDBA",
            Password = Spec.Password ?? "",
        };
        return new DmConnection(b.ConnectionString);
    }

    public override async Task<string> TestAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT BANNER FROM V$VERSION WHERE ROWNUM = 1";
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? "DM8" : v.ToString() ?? "DM8";
    }

    public override async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        // DM 单库：V$DATABASE 仅一行
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NAME FROM V$DATABASE";
        var list = new List<DatabaseInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new DatabaseInfo(reader.GetString(0)));
        return list.Count > 0 ? list : [new DatabaseInfo("DM")];
    }

    public override async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(string database, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        // 排除系统模式；排序保证稳定
        cmd.CommandText =
            "SELECT USERNAME FROM ALL_USERS WHERE USERNAME NOT IN " +
            "('SYS','SYSTEM','SYSDBA','SYSAUDITOR','SYSSSO','CTISYS','OUTRNO','SYSREP','DBSNMP') ORDER BY USERNAME";
        var list = new List<SchemaInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new SchemaInfo(reader.GetString(0)));
        return list;
    }

    public override async Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        var owner = await ResolveOwnerAsync(conn, schema, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT TABLE_NAME, 'table' AS OBJ_KIND, NUM_ROWS FROM ALL_TABLES " +
            "WHERE OWNER = ? AND SECONDARY = 'N' " +
            "UNION ALL " +
            "SELECT VIEW_NAME, 'view', NULL FROM ALL_VIEWS WHERE OWNER = ? " +
            "ORDER BY 1";
        AddParam(cmd, owner.ToUpperInvariant());
        AddParam(cmd, owner.ToUpperInvariant());
        var list = new List<TableSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(1) == "view" ? "view" : "table";
            long? rows = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            list.Add(new TableSummary(reader.GetString(0), kind, rows));
        }
        return list;
    }

    public override async Task<CanonicalTable> GetTableAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        var conn = await GetConnAsync(database, ct);
        var owner = (await ResolveOwnerAsync(conn, schema, ct)).ToUpperInvariant();
        var tableName = table.ToUpperInvariant(); // DM 大小写敏感：用 UPPER 比较定位，返回模型保留原样名

        // 表 / 视图判定
        var isView = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT 'table' FROM ALL_TABLES WHERE OWNER = ? AND TABLE_NAME = ? " +
                "UNION ALL " +
                "SELECT 'view' FROM ALL_VIEWS WHERE OWNER = ? AND TABLE_NAME = ?";
            AddParam(cmd, owner);
            AddParam(cmd, tableName);
            AddParam(cmd, owner);
            AddParam(cmd, tableName);
            var kind = await cmd.ExecuteScalarAsync(ct)
                       ?? throw new ArkException(ArkErrorCodes.TableNotFound, $"表 {owner}.{table} 不存在", 404);
            isView = (string)kind == "view";
        }

        // 列（DATA_DEFAULT 为 LONG：按序读取，null 忽略）
        var columns = new List<CanonicalColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT COLUMN_NAME, DATA_TYPE, DATA_LENGTH, CHAR_LENGTH, DATA_PRECISION, DATA_SCALE, " +
                "       NULLABLE, DATA_DEFAULT, COLUMN_ID " +
                "FROM ALL_TAB_COLUMNS WHERE OWNER = ? AND TABLE_NAME = ? ORDER BY COLUMN_ID";
            AddParam(cmd, owner);
            AddParam(cmd, tableName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var dataType = r.GetString(1);
                var dataLength = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture);
                var charLength = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture);
                int? precision = r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture);
                int? scale = r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5), CultureInfo.InvariantCulture);
                var nullable = r.GetString(6) == "Y";
                var dflt = r.IsDBNull(7) ? null : r.GetString(7)?.TrimEnd();
                // 自增：ALL_TAB_COLUMNS 无直接标记，兼容 Oracle 的 ALL_IDENTITY_COLUMNS；查询失败按 0 处理
                var isAuto = await IsIdentityColumnAsync(conn, owner, tableName, name, ct);
                var dfltExpr = isAuto ? null : (string.IsNullOrEmpty(dflt) ? null : dflt);
                columns.Add(new CanonicalColumn
                {
                    Name = name,
                    Type = TypeMapper.ToCanonical(ArkDialect.DM8, dataType,
                        charLength > 0 ? charLength : dataLength, precision, scale),
                    Nullable = nullable,
                    DefaultValueSql = dfltExpr,
                    DefaultKind = isAuto
                        ? ValueExprKind.None
                        : FunctionTranslator.Recognize(ArkDialect.DM8, dfltExpr),
                    IsAutoIncrement = isAuto,
                });
            }
        }

        // 主键（有序）
        var pkColumns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT cols.COLUMN_NAME FROM ALL_CONSTRAINTS c " +
                "JOIN ALL_CONS_COLUMNS cols ON c.OWNER = cols.OWNER AND c.CONSTRAINT_NAME = cols.CONSTRAINT_NAME " +
                "WHERE c.CONSTRAINT_TYPE = 'P' AND c.OWNER = ? AND c.TABLE_NAME = ? ORDER BY cols.POSITION";
            AddParam(cmd, owner);
            AddParam(cmd, tableName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) pkColumns.Add(r.GetString(0));
        }
        columns = columns.Select(c => c with { IsPrimaryKey = pkColumns.Contains(c.Name) }).ToList();

        // 索引（排除主键索引：与 PK 同名即跳过）
        var indexes = new List<CanonicalIndex>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT i.INDEX_NAME, i.UNIQUENESS, ic.COLUMN_NAME, ic.COLUMN_POSITION " +
                "FROM ALL_INDEXES i " +
                "JOIN ALL_IND_COLUMNS ic ON i.OWNER = ic.INDEX_OWNER AND i.INDEX_NAME = ic.INDEX_NAME " +
                "WHERE i.TABLE_OWNER = ? AND i.TABLE_NAME = ? " +
                "ORDER BY i.INDEX_NAME, ic.COLUMN_POSITION";
            AddParam(cmd, owner);
            AddParam(cmd, tableName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byName = new Dictionary<string, (bool Unique, List<string> Cols)>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                if (!byName.TryGetValue(name, out var e))
                {
                    e = (r.GetString(1) == "UNIQUE", []);
                    byName[name] = e;
                }
                e.Cols.Add(r.GetString(2));
            }
            var pkIndexNames = await LoadPkIndexNamesAsync(conn, owner, tableName, ct);
            indexes.AddRange(byName
                .Where(kv => !pkIndexNames.Contains(kv.Key))
                .Select(kv => new CanonicalIndex
                {
                    Name = kv.Key,
                    Columns = kv.Value.Cols,
                    IsUnique = kv.Value.Unique,
                    IsPrimaryKey = false,
                }));
        }

        // 外键：CONSTRAINT_TYPE='R'，经 R_CONSTRAINT_NAME 反查引用列
        var fks = new List<CanonicalForeignKey>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.CONSTRAINT_NAME, cols.COLUMN_NAME, c.R_OWNER, c.R_CONSTRAINT_NAME, c.DELETE_RULE " +
                "FROM ALL_CONSTRAINTS c " +
                "JOIN ALL_CONS_COLUMNS cols ON c.OWNER = cols.OWNER AND c.CONSTRAINT_NAME = cols.CONSTRAINT_NAME " +
                "WHERE c.CONSTRAINT_TYPE = 'R' AND c.OWNER = ? AND c.TABLE_NAME = ? " +
                "ORDER BY c.CONSTRAINT_NAME, cols.POSITION";
            AddParam(cmd, owner);
            AddParam(cmd, tableName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byName = new Dictionary<string, (List<string> Cols, string ROwner, string RConstraint, string? OnDelete)>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                if (!byName.TryGetValue(name, out var e))
                {
                    var rule = r.IsDBNull(4) ? null : r.GetString(4);
                    e = ([], r.GetString(2), r.GetString(3), rule is "NO ACTION" ? null : rule);
                    byName[name] = e;
                }
                e.Cols.Add(r.GetString(1));
            }
            foreach (var (name, e) in byName)
            {
                var (refTable, refCols) = await LoadReferencedAsync(conn, e.ROwner, e.RConstraint, ct);
                if (refTable is null) continue;
                fks.Add(new CanonicalForeignKey
                {
                    Name = name,
                    Columns = e.Cols,
                    ReferencedTable = refTable,
                    ReferencedColumns = refCols,
                    OnDelete = e.OnDelete,
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
        var conn = await GetConnAsync(database, ct);
        var owner = (await ResolveOwnerAsync(conn, schema, ct)).ToUpperInvariant();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DBMS_METADATA.GET_DDL('TABLE', ?, ?) FROM DUAL";
        AddParam(cmd, table.ToUpperInvariant());
        AddParam(cmd, owner);
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && !r.IsDBNull(0)) return r.GetString(0);
            return null;
        }
        catch (DmException)
        {
            // DBMS_METADATA 不可用（权限/组件缺失）→ 无 DDL 原文
            return null;
        }
    }

    // ------------------------------------------------ 同步运行时 ------------------------------------------------

    public override async Task<string?> GetViewDefinitionAsync(
        string database, string? schema, string view, CancellationToken ct = default)
    {
        Identifier.EnsureValid(view, "视图名");
        var conn = await GetConnAsync(database, ct);
        var owner = (await ResolveOwnerAsync(conn, schema, ct)).ToUpperInvariant();
        await using var cmd = conn.CreateCommand();
        // ALL_VIEWS.TEXT 为 LONG：GetString 读取
        cmd.CommandText = "SELECT TEXT FROM ALL_VIEWS WHERE OWNER = ? AND VIEW_NAME = ?";
        AddParam(cmd, owner);
        AddParam(cmd, view.ToUpperInvariant());
        try
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct) && !r.IsDBNull(0)) return r.GetString(0);
            return null;
        }
        catch (DmException)
        {
            return null;
        }
    }

    public override Task<IReadOnlyList<ExtensionDependency>> CheckFunctionsAsync(
        string database, IReadOnlyList<string> functions, CancellationToken ct = default)
    {
        // DM8 无扩展体系：默认值表达式依赖 FunctionTranslator 翻译，无需检查插件函数
        return Task.FromResult<IReadOnlyList<ExtensionDependency>>([]);
    }

    public override async Task<ISnapshotSession> BeginSnapshotAsync(string database, CancellationToken ct = default)
    {
        var conn = CreateConnection(database);
        await conn.OpenAsync(ct);
        var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        return new DmSnapshotSession(conn, tx);
    }

    private sealed class DmSnapshotSession(DbConnection conn, DbTransaction tx) : ISnapshotSession
    {
        public DbConnection Connection => conn;
        public string Description => "READ COMMITTED";

        public async ValueTask DisposeAsync()
        {
            try
            {
                await tx.RollbackAsync(CancellationToken.None);
                await tx.DisposeAsync();
            }
            catch { /* 尽力回滚 */ }
            await conn.DisposeAsync();
        }
    }

    public override async Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default)
    {
        var conn = await OpenConnectionAsync(table.Database, ct);
        return new DmBatchWriter(conn, Ddl.TableName(table), columns);
    }

    /// <summary>DM 批量写入通道：单事务 + 多值 INSERT 分批（参数上限按 MaxBatchParams 65000 摊行数）。</summary>
    private sealed class DmBatchWriter(DbConnection conn, string tableName, IReadOnlyList<string> columns) : IBulkWriter
    {
        // 65000 参数上限 / 列数 = 每批行数，封顶 500 控制单语句体积
        private readonly int _batchRows = Math.Max(1, Math.Min(500, 65000 / Math.Max(1, columns.Count)));
        private readonly List<object?[]> _buffer = [];
        private DbTransaction? _tx;

        public long RowsWritten { get; private set; }

        public string Channel => "batched INSERT";

        public async Task WriteAsync(object?[] values, CancellationToken ct = default)
        {
            _buffer.Add(values);
            if (_buffer.Count >= _batchRows) await FlushAsync(ct);
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
            var colSql = string.Join(", ", columns.Select(EscapeQuote));
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = _tx;
            var groups = new List<string>();
            foreach (var row in _buffer)
            {
                if (row.Length != columns.Count)
                    throw new ArgumentException($"行值数量 {row.Length} 与列数 {columns.Count} 不一致");
                // DM 使用 ? 位置参数，避免 @name 语法兼容性问题
                groups.Add("(" + string.Join(", ", columns.Select(_ => "?")) + ")");
                for (var c = 0; c < columns.Count; c++)
                {
                    var p = cmd.CreateParameter();
                    p.Value = CellValue.ToParam(row[c], ArkDialect.DM8);
                    cmd.Parameters.Add(p);
                }
            }
            cmd.CommandText = $"INSERT INTO {tableName} ({colSql}) VALUES {string.Join(", ", groups)}";
            await cmd.ExecuteNonQueryAsync(ct);
            RowsWritten += _buffer.Count;
            _buffer.Clear();
        }

        private static string EscapeQuote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

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

    public override Task<ExplainResponse> ExplainAsync(
        string database, string sql, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        // DM 返回单列计划行文本
        return ReadPlanAsync(database, "EXPLAIN " + sql, sql, timeoutSeconds, ct);
    }

    // ------------------------------------------------ 内部工具 ------------------------------------------------

    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    /// <summary>schema 缺省时回落到当前登录用户的模式（DM ≈ 当前用户模式）。</summary>
    private static async Task<string> ResolveOwnerAsync(DbConnection conn, string? schema, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(schema)) return schema;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT USER FROM DUAL";
        var user = await cmd.ExecuteScalarAsync(ct);
        if (user is null or DBNull || string.IsNullOrWhiteSpace(user.ToString()))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "无法确定当前模式，请在连接中显式指定 schema");
        return user.ToString()!;
    }

    private static void AddParam(DbCommand cmd, string value)
    {
        var p = cmd.CreateParameter();
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>自增列判定：ALL_IDENTITY_COLUMNS 兼容查询；视图/旧版本无该视图时按 0（非自增）处理。</summary>
    private static async Task<bool> IsIdentityColumnAsync(
        DbConnection conn, string owner, string table, string column, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM ALL_IDENTITY_COLUMNS " +
                "WHERE OWNER = ? AND TABLE_NAME = ? AND COLUMN_NAME = ?";
            AddParam(cmd, owner);
            AddParam(cmd, table);
            AddParam(cmd, column);
            var n = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return n > 0;
        }
        catch (DmException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlySet<string>> LoadPkIndexNamesAsync(
        DbConnection conn, string owner, string table, CancellationToken ct)
    {
        var set = new HashSet<string>(IgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT INDEX_NAME FROM ALL_CONSTRAINTS " +
            "WHERE CONSTRAINT_TYPE = 'P' AND OWNER = ? AND TABLE_NAME = ? AND INDEX_NAME IS NOT NULL";
        AddParam(cmd, owner);
        AddParam(cmd, table);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>反查被引用约束：返回 (引用表名, 引用列列表，按 POSITION)。</summary>
    private static async Task<(string? Table, List<string> Columns)> LoadReferencedAsync(
        DbConnection conn, string rOwner, string rConstraint, CancellationToken ct)
    {
        var cols = new List<string>();
        string? table = null;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT rc.TABLE_NAME, rcc.COLUMN_NAME " +
            "FROM ALL_CONSTRAINTS rc " +
            "JOIN ALL_CONS_COLUMNS rcc ON rc.OWNER = rcc.OWNER AND rc.CONSTRAINT_NAME = rcc.CONSTRAINT_NAME " +
            "WHERE rc.OWNER = ? AND rc.CONSTRAINT_NAME = ? ORDER BY rcc.POSITION";
        AddParam(cmd, rOwner);
        AddParam(cmd, rConstraint);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            table ??= r.GetString(0);
            cols.Add(r.GetString(1));
        }
        return (table, cols);
    }
}
