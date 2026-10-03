using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Responses;
using Ark.Core.Sync;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using Npgsql;

namespace Ark.Providers.PostgreSQL;

public sealed class PostgreSqlProvider : ProviderBase
{
    public PostgreSqlProvider(ConnectionSpec spec) : base(spec) { }

    public override ArkDialect Dialect => ArkDialect.PostgreSQL;

    public override DbCapabilities Capabilities { get; } =
        new(SupportsSchemas: true, SupportsMultipleDatabases: true, SupportsTruncate: true,
            SupportsDropColumn: true, SupportsAlterColumnType: true, SupportsIndexConcurrently: true,
            SupportsBulkCopy: true, SupportsConsistentSnapshot: true, ServerKind: "PostgreSQL");

    public override IDdlGenerator Ddl { get; } = new PostgreSqlDdlGenerator();

    protected override DbConnection CreateConnection(string? database)
    {
        if (string.IsNullOrWhiteSpace(Spec.Host))
            throw new ArkException(ArkErrorCodes.ConnectionConfigInvalid, "PostgreSQL 连接缺少主机地址");
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = Spec.Host,
            Port = Spec.Port ?? 5432,
            Username = Spec.Username ?? "postgres",
            Password = Spec.Password ?? "",
            Database = string.IsNullOrEmpty(database) ? (Spec.Database ?? "postgres") : database,
        };
        return new NpgsqlConnection(b.ConnectionString);
    }

    protected override IReadOnlySet<string> BuiltinFunctions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "now", "current_timestamp", "current_date", "current_time", "coalesce", "concat", "gen_random_uuid" };

    public override async Task<string> TestAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version()";
        return (string?)(await cmd.ExecuteScalarAsync(ct)) ?? "PostgreSQL";
    }

    public override async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken ct = default)
    {
        var conn = await GetConnAsync(null, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY 1";
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
            "SELECT nspname FROM pg_namespace WHERE nspname NOT LIKE 'pg\\_%' AND nspname <> 'information_schema' ORDER BY 1";
        var list = new List<SchemaInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(new SchemaInfo(reader.GetString(0)));
        return list;
    }

    public override async Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        var s = schema ?? "public";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.relname, c.relkind, c.reltuples::bigint FROM pg_class c " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = @s AND c.relkind IN ('r','p','v','m') ORDER BY 1";
        cmd.Parameters.Add(new NpgsqlParameter("@s", s));
        var list = new List<TableSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var kind = ReadPgChar(reader, 1) is "v" or "m" ? "view" : "table";
            long? rows = reader.IsDBNull(2) ? null : reader.GetInt64(2);
            list.Add(new TableSummary(reader.GetString(0), kind, rows < 0 ? null : rows));
        }
        return list;
    }

    /// <summary>
    /// pg_get_expr 返回的默认值常带显式 cast 尾巴（如 'pending'::character varying、0::numeric），
    /// 同步到其他方言前剥离（字符串字面量内容已带引号，cast 只出现在表达式末尾）。
    /// </summary>
    private static string? StripPgCasts(string? expr)
    {
        if (expr is null) return null;
        var r = expr.Trim();
        while (true)
        {
            var m = RegexCastTail.Match(r);
            if (!m.Success || m.Index == 0) break;
            r = r[..m.Index].TrimEnd();
        }
        return r;
    }

    private static readonly System.Text.RegularExpressions.Regex RegexCastTail =
        new(@"::[a-zA-Z_][\w\s]*(\([\d,\s]*\))?\s*(precision\s*)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>pg 的 "char"（单字节）类型：Npgsql 需按 char 读取，GetString 会抛 NotSupportedException。</summary>
    private static string ReadPgChar(System.Data.Common.DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return "";
        var v = r.GetValue(ordinal);
        return v switch
        {
            char c => c.ToString(),
            string s => s,
            _ => v.ToString() ?? "",
        };
    }

    public override async Task<CompletionSchema> GetCompletionSchemaAsync(
        string database, string? schema, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        var s = schema ?? "public";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.relname, c.relkind, a.attname, format_type(a.atttypid, a.atttypmod) " +
            "FROM pg_class c " +
            "JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped " +
            "WHERE n.nspname = @s AND c.relkind IN ('r','p','v','m') " +
            "ORDER BY c.relname, a.attnum";
        cmd.Parameters.Add(new NpgsqlParameter("@s", s));

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
                kinds[name] = ReadPgChar(reader, 1) is "v" or "m" ? "view" : "table";
            }
            cols.Add(new CompletionColumn(reader.GetString(2), reader.GetString(3)));
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
        EnsureSelectable(sql);
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        // 不带 ANALYZE：仅生成计划不执行；FORMAT JSON 结构化后 pretty 输出
        cmd.CommandText = "EXPLAIN (FORMAT JSON) " + sql;
        cmd.CommandTimeout = timeoutSeconds;
        var raw = await cmd.ExecuteScalarAsync(ct) as string ?? "[]";
        using var doc = JsonDocument.Parse(raw);
        return new ExplainResponse(JsonSerializer.Serialize(doc.RootElement, Indented));
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public override async Task<CanonicalTable> GetTableAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        Identifier.EnsureValid(table, "表名");
        Identifier.EnsureValid(schema ?? "public", "schema 名");
        var conn = await GetConnAsync(database, ct);
        var s = schema ?? "public";

        var isView = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.relkind FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "WHERE n.nspname = @s AND c.relname = @t";
            cmd.Parameters.Add(new NpgsqlParameter("@s", s));
            cmd.Parameters.Add(new NpgsqlParameter("@t", table));
            var kindObj = await cmd.ExecuteScalarAsync(ct)
                          ?? throw new ArkException(ArkErrorCodes.TableNotFound, $"表 {s}.{table} 不存在", 404);
            var kind = kindObj switch
            {
                char c => c.ToString(),
                string str => str,
                _ => kindObj.ToString() ?? "",
            };
            isView = kind is "v" or "m";
        }

        // 列
        var columns = new List<CanonicalColumn>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT a.attname, format_type(a.atttypid, a.atttypmod) AS col_type, a.attnotnull, " +
                "       pg_get_expr(d.adbin, d.adrelid) AS col_default, " +
                "       col_description(a.attrelid, a.attnum) AS col_comment, " +
                "       coalesce(a.attgenerated::text, '') AS attgenerated, " +
                "       coalesce(a.attidentity::text, '') AS attidentity " +
                "FROM pg_attribute a " +
                "JOIN pg_class c ON c.oid = a.attrelid " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum " +
                "WHERE n.nspname = @s AND c.relname = @t AND a.attnum > 0 AND NOT a.attisdropped " +
                "ORDER BY a.attnum";
            cmd.Parameters.Add(new NpgsqlParameter("@s", s));
            cmd.Parameters.Add(new NpgsqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var type = r.GetString(1);
                var notNull = r.GetBoolean(2);
                var dflt = r.IsDBNull(3) ? null : r.GetString(3);
                var comment = r.IsDBNull(4) ? null : r.GetString(4);
                var generated = r.GetString(5);
                var identity = r.IsDBNull(6) ? "" : r.GetString(6);
                // IDENTITY 列无 nextval 默认值，需读 attidentity；serial 系列才有 nextval
                var isAuto = identity.Length > 0 || (dflt is not null && dflt.Contains("nextval(", StringComparison.OrdinalIgnoreCase));
                var dfltExpr = isAuto ? null : StripPgCasts(dflt);
                columns.Add(new CanonicalColumn
                {
                    Name = name,
                    Type = TypeMapper.ToCanonical(ArkDialect.PostgreSQL, type, null, null, null),
                    Nullable = !notNull,
                    DefaultValueSql = dfltExpr,
                    DefaultKind = generated.Length > 0 || isAuto
                        ? ValueExprKind.None
                        : FunctionTranslator.Recognize(ArkDialect.PostgreSQL, dfltExpr),
                    IsAutoIncrement = isAuto,
                    Comment = comment,
                    IsGenerated = generated.Length > 0,
                });
            }
        }

        // 主键（有序）
        var pkColumns = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT a.attname FROM pg_index i " +
                "CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) " +
                "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum " +
                "WHERE i.indisprimary AND i.indrelid = (" +
                "  SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "  WHERE n.nspname = @s AND c.relname = @t) ORDER BY k.ord";
            cmd.Parameters.Add(new NpgsqlParameter("@s", s));
            cmd.Parameters.Add(new NpgsqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) pkColumns.Add(r.GetString(0));
        }
        columns = columns.Select(c => c with { IsPrimaryKey = pkColumns.Contains(c.Name) }).ToList();

        // 索引（排除主键）
        var indexes = new List<CanonicalIndex>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT i.relname, ix.indisunique, ix.indpred IS NOT NULL AS is_partial, a.attname " +
                "FROM pg_index ix " +
                "JOIN pg_class i ON i.oid = ix.indexrelid " +
                "JOIN pg_class c ON c.oid = ix.indrelid " +
                "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "CROSS JOIN LATERAL unnest(ix.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord) " +
                "JOIN pg_attribute a ON a.attrelid = ix.indrelid AND a.attnum = k.attnum " +
                "WHERE n.nspname = @s AND c.relname = @t AND NOT ix.indisprimary " +
                "ORDER BY i.relname, k.ord";
            cmd.Parameters.Add(new NpgsqlParameter("@s", s));
            cmd.Parameters.Add(new NpgsqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var byName = new Dictionary<string, (bool Unique, bool Partial, List<string> Cols)>();
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                if (!byName.TryGetValue(name, out var e))
                {
                    e = (r.GetBoolean(1), r.GetBoolean(2), []);
                    byName[name] = e;
                }
                e.Cols.Add(r.GetString(3));
            }
            indexes.AddRange(byName.Select(kv => new CanonicalIndex
            {
                Name = kv.Key,
                Columns = kv.Value.Cols,
                IsUnique = kv.Value.Unique,
                IsPrimaryKey = false,
                WhereClause = kv.Value.Partial ? "(partial index)" : null,
            }));
        }

        // 外键（解析 pg_get_constraintdef）
        var fks = new List<CanonicalForeignKey>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT con.conname, pg_get_constraintdef(con.oid) FROM pg_constraint con " +
                "WHERE con.contype = 'f' AND con.conrelid = (" +
                "  SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                "  WHERE n.nspname = @s AND c.relname = @t)";
            cmd.Parameters.Add(new NpgsqlParameter("@s", s));
            cmd.Parameters.Add(new NpgsqlParameter("@t", table));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var fk = ParseForeignKeyDef(r.GetString(0), r.GetString(1));
                if (fk is not null) fks.Add(fk);
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

    /// <summary>解析 FOREIGN KEY (a, b) REFERENCES tbl (x, y) ON DELETE ... ON UPDATE ...</summary>
    private static CanonicalForeignKey? ParseForeignKeyDef(string name, string def)
    {
        try
        {
            var fkIdx = def.IndexOf("FOREIGN KEY", StringComparison.OrdinalIgnoreCase);
            var refIdx = def.IndexOf("REFERENCES", StringComparison.OrdinalIgnoreCase);
            if (fkIdx < 0 || refIdx < 0) return null;

            // FOREIGN KEY (a, b) REFERENCES tbl (x, y) ON DELETE/UPDATE ...
            var colsPart = def[(fkIdx + "FOREIGN KEY".Length)..refIdx].Trim();
            var cols = ExtractParenGroup(colsPart) ?? [];

            var afterRef = def[(refIdx + "REFERENCES".Length)..].Trim();
            // 引用表名在首个 '('（带列组）或空格处结束
            var paren = afterRef.IndexOf('(');
            var space = FindTopLevelSpace(afterRef);
            var refEnd = paren >= 0 && (space < 0 || paren < space) ? paren : space;
            var refTable = afterRef[..refEnd].Trim().Trim('"', '`');
            var rest = afterRef[refEnd..].Trim();

            var refCols = new List<string>();
            var parenStart = rest.IndexOf('(');
            if (parenStart >= 0)
            {
                var depth = 0;
                var parenEnd = -1;
                for (var i = parenStart; i < rest.Length; i++)
                {
                    if (rest[i] == '(') depth++;
                    else if (rest[i] == ')')
                    {
                        depth--;
                        if (depth == 0) { parenEnd = i; break; }
                    }
                }
                if (parenEnd > parenStart)
                    refCols = FunctionTranslator.SplitTopLevelArgs(rest[(parenStart + 1)..parenEnd]);
                rest = rest[(parenEnd + 1)..].Trim();
            }

            string? onDelete = null, onUpdate = null;
            // "ON DELETE CASCADE" → 动作词是第 3 个 token（此前错取第 2 个得到 "DELETE"）
            var odIdx = rest.IndexOf("ON DELETE", StringComparison.OrdinalIgnoreCase);
            var ouIdx = rest.IndexOf("ON UPDATE", StringComparison.OrdinalIgnoreCase);
            if (odIdx >= 0)
                onDelete = rest[odIdx..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(2).FirstOrDefault();
            if (ouIdx >= 0)
                onUpdate = rest[ouIdx..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(2).FirstOrDefault();

            return new CanonicalForeignKey
            {
                Name = name,
                Columns = cols,
                ReferencedTable = refTable,
                ReferencedColumns = refCols,
                OnDelete = onDelete,
                OnUpdate = onUpdate,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>提取首个括号组内的顶层参数（如 "(a, b)" → ["a", "b"]）；无括号返回 null。</summary>
    private static List<string>? ExtractParenGroup(string s)
    {
        var start = s.IndexOf('(');
        if (start < 0) return null;
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return FunctionTranslator.SplitTopLevelArgs(s[(start + 1)..i]);
            }
        }
        return null;
    }

    private static int FindTopLevelSpace(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '(': depth++; break;
                case ')': depth--; break;
                case ' ' when depth == 0: return i;
            }
        }
        return s.Length;
    }

    public override async Task<string?> GetCreateTableSqlAsync(
        string database, string? schema, string table, CancellationToken ct = default)
    {
        // PostgreSQL 无内建 SHOW CREATE；由 Canonical 模型重建等价 DDL
        var t = await GetTableAsync(database, schema, table, ct);
        var warnings = new List<string>();
        return new PostgreSqlDdlGenerator().CreateTable(t, warnings).FirstOrDefault();
    }

    public override async Task<long> EstimateRowsAsync(TableRef table, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(table.Database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.reltuples::bigint FROM pg_class c JOIN pg_namespace n on n.oid = c.relnamespace " +
            "WHERE n.nspname = @s AND c.relname = @t";
        cmd.Parameters.Add(new NpgsqlParameter("@s", table.Schema ?? "public"));
        cmd.Parameters.Add(new NpgsqlParameter("@t", table.Table));
        var v = await cmd.ExecuteScalarAsync(ct);
        // reltuples<=0 表示统计信息缺失（未 ANALYZE 时为 -1/0），不能钳成 0，回退精确 COUNT(*)
        if (v is null or DBNull || Convert.ToInt64(v, CultureInfo.InvariantCulture) <= 0)
        {
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = $"SELECT COUNT(*) FROM {Ddl.TableName(table)}";
            var count = await cmd2.ExecuteScalarAsync(ct);
            return count is null or DBNull ? 0 : Convert.ToInt64(count, CultureInfo.InvariantCulture);
        }
        return Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------ 同步运行时 ------------------------------------------------

    public override async Task<string?> GetViewDefinitionAsync(
        string database, string? schema, string view, CancellationToken ct = default)
    {
        Identifier.EnsureValid(view, "视图名");
        Identifier.EnsureValid(schema ?? "public", "schema 名");
        var conn = await GetConnAsync(database, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_get_viewdef(@oid::regclass, true)";
        cmd.Parameters.Add(new NpgsqlParameter("@oid", $"\"{(schema ?? "public")}\".\"{view}\""));
        try
        {
            return await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch (PostgresException)
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
            cmd.CommandText = "START TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        return new PgSnapshotSession(conn);
    }

    private sealed class PgSnapshotSession(DbConnection conn) : ISnapshotSession
    {
        public DbConnection Connection => conn;
        public string Description => "REPEATABLE READ (read only)";

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

    public override IReadOnlyList<string> BulkLoadPragmas => ["SET synchronous_commit = off"];

    public override async Task<IBulkWriter> OpenBulkWriterAsync(
        TableRef table, IReadOnlyList<string> columns, IReadOnlyList<CanonicalColumn> targetCols, CancellationToken ct = default)
    {
        var conn = (NpgsqlConnection)await OpenConnectionAsync(table.Database, ct);
        var colSql = string.Join(", ", columns.Select(Ddl.Quote));
        var writer = await conn.BeginTextImportAsync($"COPY {Ddl.TableName(table)} ({colSql}) FROM STDIN", ct);
        return new PgCopyWriter(writer, columns.Count);
    }

    /// <summary>COPY text 格式写入器：TSV 转义；null=\N；bytea=\x hex；bool=t/f。Dispose 即完成 COPY。</summary>
    private sealed class PgCopyWriter(TextWriter inner, int colCount) : IBulkWriter
    {
        public long RowsWritten { get; private set; }

        public string Channel => "COPY (text format)";

        public async Task WriteAsync(object?[] values, CancellationToken ct = default)
        {
            for (var i = 0; i < colCount; i++)
            {
                if (i > 0) await inner.WriteAsync('\t');
                await inner.WriteAsync(Escape(values[i]));
            }
            await inner.WriteAsync('\n');
            RowsWritten++;
        }

        public async Task CompleteAsync(CancellationToken ct = default) => await inner.FlushAsync(ct);

        public async ValueTask DisposeAsync() => await inner.DisposeAsync();
    }

    internal static string Escape(object? v) => v switch
    {
        null => "\\N",
        DBNull => "\\N",
        bool b => b ? "t" : "f",
        byte[] bytes => "\\x" + Convert.ToHexString(bytes).ToLowerInvariant(),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        string s => EscapeText(s),
        _ => EscapeText(v.ToString() ?? ""),
    };

    private static string EscapeText(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\t", "\\t")
        .Replace("\n", "\\n")
        .Replace("\r", "\\r");

    public override async Task<IReadOnlyList<ExtensionDependency>> CheckFunctionsAsync(
        string database, IReadOnlyList<string> functions, CancellationToken ct = default)
    {
        var conn = await GetConnAsync(database, ct);
        var versionNum = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT current_setting('server_version_num')::int";
            versionNum = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT extname FROM pg_extension";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) installed.Add(r.GetString(0));
        }
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pg_available_extensions";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) available.Add(r.GetString(0));
        }

        var result = new List<ExtensionDependency>();
        foreach (var f in functions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (f.Equals("gen_random_uuid", StringComparison.OrdinalIgnoreCase))
            {
                if (versionNum >= 130000)
                {
                    result.Add(new ExtensionDependency { Name = f, InstallSql = "", Present = true, Note = "PostgreSQL 13+ 内置" });
                }
                else if (installed.Contains("pgcrypto"))
                {
                    result.Add(new ExtensionDependency { Name = f, InstallSql = "", Present = true, Note = "由 pgcrypto 扩展提供" });
                }
                else
                {
                    result.Add(new ExtensionDependency
                    {
                        Name = f,
                        InstallSql = available.Contains("pgcrypto") ? "CREATE EXTENSION IF NOT EXISTS pgcrypto;" : "",
                        Present = false,
                        Note = "PostgreSQL < 13 需要 pgcrypto 扩展提供 gen_random_uuid()",
                    });
                }
                continue;
            }
            if (f.StartsWith("uuid_generate", StringComparison.OrdinalIgnoreCase))
            {
                var ok = installed.Contains("uuid-ossp");
                result.Add(new ExtensionDependency
                {
                    Name = f,
                    InstallSql = ok ? "" : (available.Contains("uuid-ossp") ? "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";" : ""),
                    Present = ok,
                    Note = ok ? "由 uuid-ossp 扩展提供" : "需要 uuid-ossp 扩展",
                });
                continue;
            }
            result.Add(new ExtensionDependency
            {
                Name = f,
                InstallSql = "",
                Present = BuiltinFunctions.Contains(f),
                Note = BuiltinFunctions.Contains(f) ? null : $"PostgreSQL 未能确认支持函数 {f}",
            });
        }
        return result;
    }
}
