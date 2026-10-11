using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;
using Ark.Providers.DuckDB;
using Ark.Providers.SQLite;
using Ark.Sync;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ark.Integration.Tests;

/// <summary>
/// SQLite → DuckDB 真实链路集成测试（临时文件，无 Docker 依赖）：
/// 结构转换建表 → 全量复制（批量 INSERT 通道）→ 元数据/行读/复合类型断言 → 变更集提交。
/// </summary>
public class DuckDbSyncTests : IAsyncLifetime
{
    private readonly string _srcPath = Path.Combine(Path.GetTempPath(), $"ark_dk_{Guid.NewGuid():N}.db");
    private readonly string _tgtPath = Path.Combine(Path.GetTempPath(), $"ark_dk_{Guid.NewGuid():N}.duckdb");

    public Task InitializeAsync()
    {
        ExecSqlite(_srcPath, """
            CREATE TABLE users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                age INT DEFAULT 0,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO users (name, age) VALUES ('alice', 30), ('bob', 25), ('carol', 35);
            """);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (File.Exists(_srcPath)) File.Delete(_srcPath);
        if (File.Exists(_tgtPath)) File.Delete(_tgtPath);
        await Task.CompletedTask;
    }

    private static void ExecSqlite(string path, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static IDbProvider SqliteSource(string path) =>
        new SqliteProvider(new ConnectionSpec(ArkDialect.SQLite, null, null, null, null, null, path, false,
            new Dictionary<string, string>()));

    private IDbProvider DuckTarget(bool readOnly = false) =>
        new DuckDbProvider(new ConnectionSpec(ArkDialect.DuckDB, null, null, null, null, null, _tgtPath, readOnly,
            new Dictionary<string, string>()));

    private static TableSelection Sel() => new() { Database = "main", Schema = "main", Table = "users" };

    private static SyncPlanRequest Opts() => new()
    {
        SourceConnectionId = Guid.NewGuid(),
        SourceDatabase = "main",
        TargetConnectionId = Guid.NewGuid(),
        TargetDatabase = "main",
        Mode = SyncMode.StructureAndData,
        DataMethod = DataSyncMethod.FullCopy,
        ConflictMode = ConflictMode.Truncate,
        BatchSize = 2,
        Tables = [Sel()],
    };

    [Fact]
    public async Task TestAsync_ReturnsServerVersion()
    {
        await using var target = DuckTarget();
        var version = await target.TestAsync();
        Assert.StartsWith("v", version);
    }

    [Fact]
    public async Task Metadata_ListsDatabasesTablesAndColumns()
    {
        await using var target = DuckTarget();
        await SeedTargetWithTableAsync(target);

        Assert.Equal(["main"], (await target.ListDatabasesAsync()).Select(d => d.Name));
        Assert.Equal(["main"], (await target.ListSchemasAsync("main")).Select(s => s.Name));

        var tables = await target.ListTablesAsync("main", "main");
        var users = Assert.Single(tables, t => t.Name == "users");
        Assert.Equal("table", users.Kind);

        var meta = await target.GetTableAsync("main", "main", "users");
        Assert.Equal(["id"], meta.PrimaryKeyColumns);
        Assert.False(meta.IsView);
        Assert.Equal(4, meta.Columns.Count);

        var id = meta.Columns.Single(c => c.Name == "id");
        Assert.Equal(CanonicalTypeId.Int64, id.Type.Id); // SQLite INTEGER → BIGINT
        Assert.False(id.Nullable);                        // PK 非空
        var name = meta.Columns.Single(c => c.Name == "name");
        Assert.Equal(CanonicalTypeId.VarChar, name.Type.Id); // TEXT → VARCHAR（DuckDB 定向）
        Assert.False(name.Nullable);
        var createdAt = meta.Columns.Single(c => c.Name == "created_at");
        Assert.Equal(CanonicalTypeId.DateTime, createdAt.Type.Id);
        Assert.Equal(ValueExprKind.CurrentTimestamp, createdAt.DefaultKind); // CURRENT_TIMESTAMP 保留
    }

    [Fact]
    public async Task FullCopy_TransfersAllRowsAndValues()
    {
        await using var source = SqliteSource(_srcPath);
        await using var target = DuckTarget();
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.DuckDB);

        var warnings = new List<string>();
        var tgtRef = new TableRef("main", "main", "users");
        var created = target.Ddl.CreateTable(tgtRef, converted, warnings);
        // SQLite 自增主键 → DuckDB 无 AUTOINCREMENT，按显式值复制（警告提示语义变化）
        Assert.Contains(warnings, w => w.Contains("AUTOINCREMENT"));
        await target.ExecuteDdlAsync("main", created);
        var tgtMeta = await target.GetTableAsync("main", "main", "users");

        var engine = new DataTransferEngine();
        var stats = await engine.FullCopyAsync(
            source, target,
            new TableRef("main", "main", "users"), srcMeta,
            tgtRef, tgtMeta,
            Sel(), Opts());

        Assert.Equal(3, stats.Inserted);
        Assert.Equal("批量 INSERT（单事务）", stats.Channel); // 批量写入通道真实启用

        // 行数与值断言（经 DuckDB Provider 真实读回）
        Assert.Equal(3, await CountUsersAsync(target));
        var page = await target.ReadRowsAsync(tgtRef, tgtMeta, new RowQueryRequest { Limit = 10 });
        var byId = page.Rows.ToDictionary(r => Convert.ToInt64(r[0]), r => (string?)r[1]);
        Assert.Equal("alice", byId[1]);
        Assert.Equal("bob", byId[2]);
        Assert.Equal("carol", byId[3]);
        // age 经 INTEGER 通道原样复制
        Assert.Equal(30L, Convert.ToInt64(page.Rows.Single(r => Convert.ToInt64(r[0]) == 1)[2]));
    }

    [Fact]
    public async Task CompositeTypes_ReadBackAsText()
    {
        await using var target = DuckTarget();
        await target.ExecuteDdlAsync("main",
        [
            """
            CREATE TABLE IF NOT EXISTS "flex" (
                "id" INTEGER PRIMARY KEY,
                "tags" VARCHAR[],
                "meta" STRUCT(a INTEGER, b VARCHAR)
            )
            """,
        ]);
        await target.ExecuteDdlAsync("main",
        [
            """INSERT INTO "flex" VALUES (1, ['x','y'], {'a': 1, 'b': 'z'})""",
        ]);

        var meta = await target.GetTableAsync("main", "main", "flex");
        // 方括号写法不在映射表 → Unknown（读取路径按 duckdb_columns().data_type 兜底 CAST）
        Assert.Equal(CanonicalTypeId.Unknown, meta.Columns.Single(c => c.Name == "tags").Type.Id);
        Assert.Equal(CanonicalTypeId.Json, meta.Columns.Single(c => c.Name == "meta").Type.Id);

        // 复合类型列以 VARCHAR 文本读出（不经 override 会返回 List/Dictionary 导致 JSON 序列化失败）
        var page = await target.ReadRowsAsync(new TableRef("main", "main", "flex"), meta,
            new RowQueryRequest { Limit = 10 });
        var row = Assert.Single(page.Rows);
        Assert.IsType<string>(row[1]);
        Assert.IsType<string>(row[2]);
        Assert.Contains("x", (string)row[1]!);
    }

    [Fact]
    public async Task SnapshotAndApplyChanges_WorkOnDuckDb()
    {
        await using var target = DuckTarget();
        await SeedTargetWithTableAsync(target);
        await target.ExecuteDdlAsync("main",
            ["""INSERT INTO "users" ("id", "name", "age") VALUES (1, 'alice', 30), (2, 'bob', 25), (3, 'carol', 35)"""]);
        var meta = await target.GetTableAsync("main", "main", "users");
        var tgtRef = new TableRef("main", "main", "users");

        // 一致快照会话（驱动仅接受 Snapshot 隔离级别）
        await using (var snapshot = await target.BeginSnapshotAsync("main"))
        {
            Assert.True(snapshot.Connection.State == System.Data.ConnectionState.Open);
            await using var cmd = snapshot.Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM \"users\"";
            Assert.Equal(3L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
        }

        // 变更集：插入 + 更新 + 删除（$name 参数绑定，单事务）
        var changes = new RowChangeSet
        {
            Inserts = [new RowInsert(new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["id"] = System.Text.Json.JsonSerializer.SerializeToElement(9),
                ["name"] = System.Text.Json.JsonSerializer.SerializeToElement("dave"),
                ["age"] = System.Text.Json.JsonSerializer.SerializeToElement(40),
            })],
            Updates = [new RowUpdate(
                new Dictionary<string, System.Text.Json.JsonElement> { ["id"] = System.Text.Json.JsonSerializer.SerializeToElement(1) },
                new Dictionary<string, System.Text.Json.JsonElement> { ["age"] = System.Text.Json.JsonSerializer.SerializeToElement(31) })],
            Deletes = [new RowDelete(
                new Dictionary<string, System.Text.Json.JsonElement> { ["id"] = System.Text.Json.JsonSerializer.SerializeToElement(3) })],
        };
        var result = await target.ApplyChangesAsync(tgtRef, meta, changes);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Deleted);

        Assert.Equal(3, await CountUsersAsync(target));
        var after = await target.ReadRowsAsync(tgtRef, meta, new RowQueryRequest { Limit = 10 });
        Assert.Equal(31L, Convert.ToInt64(after.Rows.Single(r => Convert.ToInt64(r[0]) == 1)[2]));
    }

    [Fact]
    public async Task Explain_ProducesPlanText()
    {
        await using var target = DuckTarget();
        await SeedTargetWithTableAsync(target);
        var plan = await target.ExplainAsync("main", "SELECT * FROM \"users\"");
        Assert.False(string.IsNullOrWhiteSpace(plan.PlanText));
    }

    [Fact]
    public async Task Ddl_GeneratesTruncateAndIndex()
    {
        await using var target = DuckTarget();
        await SeedTargetWithTableAsync(target);

        var idx = new CanonicalIndex { Name = "idx_users_name", Columns = ["name"], IsUnique = false };
        var ref_ = new TableRef("main", "main", "users");
        Assert.NotEmpty(target.Ddl.CreateIndex(idx, ref_));
        await target.ExecuteDdlAsync("main", target.Ddl.CreateIndex(idx, ref_));

        Assert.NotEmpty(target.Ddl.TruncateTable(ref_));
        await target.ExecuteDdlAsync("main", target.Ddl.TruncateTable(ref_));
        Assert.Equal(0, await CountUsersAsync(target));
        Assert.Empty(target.Ddl.ResetAutoIncrement(ref_, "id", 100)); // DuckDB 无自增推进
    }

    private async Task SeedTargetWithTableAsync(IDbProvider target)
    {
        await using var source = SqliteSource(_srcPath);
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.DuckDB);
        await target.ExecuteDdlAsync("main", target.Ddl.CreateTable(new TableRef("main", "main", "users"), converted, new List<string>()));
    }

    private static async Task<long> CountUsersAsync(IDbProvider target)
    {
        var rs = await target.ExecuteQueryAsync("main", "SELECT COUNT(*) FROM \"users\"", 10, 30);
        return Convert.ToInt64(rs.ResultSets[0].Rows[0][0]);
    }
}
