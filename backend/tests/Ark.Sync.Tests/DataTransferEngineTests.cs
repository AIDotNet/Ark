using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;
using Ark.Providers.SQLite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Ark.Sync.Tests;

/// <summary>用真实 SQLite Provider 走通 全量复制 / 分块 Diff / 结构同步 的端到端验证。</summary>
public class DataTransferEngineTests : IAsyncLifetime
{
    private readonly string _srcPath = Path.Combine(Path.GetTempPath(), $"ark_t_{Guid.NewGuid():N}.db");
    private readonly string _tgtPath = Path.Combine(Path.GetTempPath(), $"ark_t_{Guid.NewGuid():N}.db");

    public Task InitializeAsync()
    {
        Exec(_srcPath, """
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

    private static void Exec(string path, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static IDbProvider Provider(string path, bool readOnly = false) =>
        new SqliteProvider(new ConnectionSpec(ArkDialect.SQLite, null, null, null, null, null, path, readOnly,
            new Dictionary<string, string>()));

    private static async Task<CanonicalTable> CreateTableOnTargetAsync(IDbProvider target, CanonicalTable src)
    {
        var warnings = new List<string>();
        var stmts = target.Ddl.CreateTable(src, warnings);
        await target.ExecuteDdlAsync("main", stmts);
        return await target.GetTableAsync("main", "main", "users");
    }

    private static TableSelection Sel() => new() { Database = "main", Schema = "main", Table = "users" };

    private static SyncPlanRequest Opts(
        ConflictMode conflict = ConflictMode.Error,
        DataSyncMethod method = DataSyncMethod.FullCopy,
        bool deleteExtra = false,
        int chunkRows = 2) =>
        new()
        {
            SourceConnectionId = Guid.NewGuid(),
            SourceDatabase = "main",
            TargetConnectionId = Guid.NewGuid(),
            TargetDatabase = "main",
            Mode = SyncMode.StructureAndData,
            DataMethod = method,
            ConflictMode = conflict,
            DeleteExtraRows = deleteExtra,
            ChunkRows = chunkRows,
            BatchSize = 2,
            Tables = [Sel()],
        };

    [Fact]
    public async Task FullCopy_TransfersRowsAndAlignsAutoIncrement()
    {
        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.SQLite);
        var tgtMeta = await CreateTableOnTargetAsync(target, converted);

        var engine = new DataTransferEngine();
        var stats = await engine.FullCopyAsync(
            source, target,
            new TableRef("main", "main", "users"), srcMeta,
            new TableRef("main", "main", "users"), tgtMeta,
            Sel(), Opts());

        Assert.Equal(3, stats.Inserted);
        Assert.Equal(3, await CountAsync(_tgtPath, "users"));
        Assert.Equal(3L, await ScalarAsync(_tgtPath, "SELECT MAX(id) FROM users"));
        // 自增已对齐：再插入不冲突
        Exec(_tgtPath, "INSERT INTO users (name) VALUES ('dave')");
        Assert.Equal(4L, await ScalarAsync(_tgtPath, "SELECT MAX(id) FROM users"));
    }

    [Fact]
    public async Task FullCopy_TruncateConflict_ReplacesTargetRows()
    {
        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.SQLite);
        var tgtMeta = await CreateTableOnTargetAsync(target, converted);
        Exec(_tgtPath, "INSERT INTO users (id, name, age) VALUES (1, 'old', 99)");

        var engine = new DataTransferEngine();
        await engine.FullCopyAsync(source, target,
            new TableRef("main", "main", "users"), srcMeta,
            new TableRef("main", "main", "users"), tgtMeta,
            Sel(), Opts(conflict: ConflictMode.Truncate));

        Assert.Equal(3, await CountAsync(_tgtPath, "users"));
        Assert.Equal("alice", await ScalarAsync(_tgtPath, "SELECT name FROM users WHERE id=1"));
    }

    [Fact]
    public async Task FullCopy_SkipExisting_KeepsExistingRows()
    {
        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.SQLite);
        var tgtMeta = await CreateTableOnTargetAsync(target, converted);
        Exec(_tgtPath, "INSERT INTO users (id, name, age) VALUES (1, 'keep-old', 99)");

        var engine = new DataTransferEngine();
        await engine.FullCopyAsync(source, target,
            new TableRef("main", "main", "users"), srcMeta,
            new TableRef("main", "main", "users"), tgtMeta,
            Sel(), Opts(conflict: ConflictMode.SkipExisting));

        // id1 保留旧值，新增 id2/id3
        Assert.Equal("keep-old", await ScalarAsync(_tgtPath, "SELECT name FROM users WHERE id=1"));
        Assert.Equal(3, await CountAsync(_tgtPath, "users"));
    }

    [Fact]
    public async Task ChunkedDiff_InsertsUpdatesAndDeletes()
    {
        // 目标端预置旧数据（id 1 过期、id 2 相同、id 99 多余）
        Exec(_tgtPath, """
            CREATE TABLE users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                age INT DEFAULT 0,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO users (id, name, age) VALUES (1, 'alice-old', 99), (2, 'bob', 25), (99, 'extra', 0);
            """);

        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.SQLite);
        var tgtMeta = await target.GetTableAsync("main", "main", "users");

        var engine = new DataTransferEngine();
        var stats = await engine.DiffTableAsync(
            source, target,
            new TableRef("main", "main", "users"), srcMeta,
            new TableRef("main", "main", "users"), tgtMeta,
            Sel(), Opts(method: DataSyncMethod.RowDiff, deleteExtra: true, chunkRows: 2));

        Assert.Equal(1, stats.Inserted);   // id3 carol
        Assert.Equal(1, stats.Updated);    // id1 alice
        Assert.Equal(1, stats.Deleted);    // id99 extra

        Assert.Equal("alice", await ScalarAsync(_tgtPath, "SELECT name FROM users WHERE id=1"));
        Assert.Equal(30L, await ScalarAsync(_tgtPath, "SELECT age FROM users WHERE id=1"));
        Assert.Equal(3L, await ScalarAsync(_tgtPath, "SELECT COUNT(*) FROM users"));

        // 第二轮：源删掉 id2 → 目标应删除
        Exec(_srcPath, "DELETE FROM users WHERE id = 2");
        stats = await engine.DiffTableAsync(
            source, target,
            new TableRef("main", "main", "users"), srcMeta,
            new TableRef("main", "main", "users"), tgtMeta,
            Sel(), Opts(method: DataSyncMethod.RowDiff, deleteExtra: true, chunkRows: 2));
        Assert.Equal(1, stats.Deleted);
        Assert.Equal(2L, await ScalarAsync(_tgtPath, "SELECT COUNT(*) FROM users"));
    }

    [Fact]
    public async Task ChunkedDiff_ReportMode_ProducesKeysAndSamplesWithoutApplying()
    {
        Exec(_tgtPath, """
            CREATE TABLE users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                age INT DEFAULT 0,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO users (id, name, age) VALUES (1, 'alice-old', 99);
            """);

        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.SQLite);
        var tgtMeta = await target.GetTableAsync("main", "main", "users");

        var engine = new DataTransferEngine();
        var stats = await engine.DiffTableAsync(
            source, target,
            new TableRef("main", "main", "users"), srcMeta,
            new TableRef("main", "main", "users"), tgtMeta,
            Sel(), Opts(method: DataSyncMethod.RowDiff, chunkRows: 2),
            apply: false, maxDiffKeys: 100, maxSamples: 10);

        Assert.Equal(2, stats.Inserted);
        Assert.Equal(1, stats.Updated);
        Assert.Equal(0, stats.Deleted);
        Assert.Single(stats.Keys.Updated);
        Assert.Equal(2, stats.Keys.Inserted.Count);
        Assert.Contains(stats.Samples, s => s.ChangeType == "update");
        // 未应用
        Assert.Equal(1L, await ScalarAsync(_tgtPath, "SELECT COUNT(*) FROM users"));
        Assert.Equal("alice-old", await ScalarAsync(_tgtPath, "SELECT name FROM users WHERE id=1"));
    }

    [Fact]
    public async Task ChunkedDiff_EquivalentToNaiveRowDiff_OnLargerDataset()
    {
        // 300 行、窗口 7 行（多窗口边界），目标端注入三类差异
        var src = Path.Combine(Path.GetTempPath(), $"ark_e1_{Guid.NewGuid():N}.db");
        var tgt = Path.Combine(Path.GetTempPath(), $"ark_e2_{Guid.NewGuid():N}.db");
        try
        {
            var seedSql = new System.Text.StringBuilder("""
                CREATE TABLE items (id INTEGER PRIMARY KEY, v TEXT NOT NULL, n INT DEFAULT 0);
                """);
            for (var i = 1; i <= 300; i++)
                seedSql.Append($"\nINSERT INTO items (id, v, n) VALUES ({i}, 'v{i}', {i % 7});");
            Exec(src, seedSql.ToString());

            var tgtSql = """
                CREATE TABLE items (id INTEGER PRIMARY KEY, v TEXT NOT NULL, n INT DEFAULT 0);
                INSERT INTO items (id, v, n) VALUES (1, 'CHANGED', 0), (50, 'v50', 1), (299, 'CHANGED', 3);
                INSERT INTO items (id, v, n) VALUES (500, 'EXTRA', 0), (501, 'EXTRA2', 0);
                """;
            Exec(tgt, tgtSql);

            await using var source = Provider(src);
            await using var target = Provider(tgt);
            var srcMeta = await source.GetTableAsync("main", "main", "items");
            var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.SQLite);
            var tgtMeta = await target.GetTableAsync("main", "main", "items");

            var engine = new DataTransferEngine();
            var stats = await engine.DiffTableAsync(
                source, target,
                new TableRef("main", "main", "items"), srcMeta,
                new TableRef("main", "main", "items"), tgtMeta,
                new TableSelection { Database = "main", Schema = "main", Table = "items" },
                Opts(method: DataSyncMethod.RowDiff, deleteExtra: true, chunkRows: 7));

            // 源 300 行；目标 5 行中 id50 相同、id1/id299 过期、id500/501 多余
            // 期望：插入 297、更新 2（id1/id299）、删除 2（id500/501）
            Assert.Equal(297, stats.Inserted);
            Assert.Equal(2, stats.Updated);
            Assert.Equal(2, stats.Deleted);
            // 逐行验证最终一致
            Assert.Equal(300L, await ScalarAsync(tgt, "SELECT COUNT(*) FROM items"));
            Assert.Equal("v1", await ScalarAsync(tgt, "SELECT v FROM items WHERE id=1"));
            Assert.Equal("v299", await ScalarAsync(tgt, "SELECT v FROM items WHERE id=299"));
            Assert.Equal(0L, await ScalarAsync(tgt, "SELECT COUNT(*) FROM items WHERE id IN (500, 501)"));
        }
        finally
        {
            if (File.Exists(src)) File.Delete(src);
            if (File.Exists(tgt)) File.Delete(tgt);
        }
    }

    [Fact]
    public async Task ChunkedDiff_EmptyTarget_AllRowsInserted()
    {
        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);
        var srcMeta = await source.GetTableAsync("main", "main", "users");
        var (converted, _) = TableConverter.Convert(srcMeta, ArkDialect.SQLite, ArkDialect.SQLite);
        var tgtMeta = await CreateTableOnTargetAsync(target, converted);

        var engine = new DataTransferEngine();
        var stats = await engine.DiffTableAsync(
            source, target,
            new TableRef("main", "main", "users"), srcMeta,
            new TableRef("main", "main", "users"), tgtMeta,
            Sel(), Opts(method: DataSyncMethod.RowDiff, chunkRows: 2));

        Assert.Equal(3, stats.Inserted);
        Assert.Equal(0, stats.Deleted);
        Assert.Equal(3L, await ScalarAsync(_tgtPath, "SELECT COUNT(*) FROM users"));
    }

    [Fact]
    public async Task SyncPlanner_ProducesCreateActionsForMissingTables()
    {
        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);

        var planner = new SyncPlanner();
        var plan = await planner.BuildPlanAsync(source, target, Opts(method: DataSyncMethod.FullCopy));

        var tp = Assert.Single(plan.Tables);
        Assert.False(tp.TargetTableExists);
        Assert.Contains(tp.StructureActions, a => a.Kind == "CreateTable");
        Assert.True(tp.Data!.HasPrimaryKey);
        Assert.All(tp.StructureActions, a => Assert.False(a.IsDestructive));
    }

    [Fact]
    public async Task Planner_DeferIndexes_MovesIndexCreationToPostCopy()
    {
        Exec(_srcPath, "CREATE INDEX idx_users_name ON users(name);");
        await using var source = Provider(_srcPath);
        await using var target = Provider(_tgtPath);

        var opts = Opts();
        var plan = await new SyncPlanner().BuildPlanAsync(source, target, opts with { DeferIndexes = true });
        var tp = Assert.Single(plan.Tables);
        Assert.Contains(tp.PostCopyActions, a => a.Kind == "CreateIndex");
        Assert.DoesNotContain(tp.StructureActions, a => a.Kind == "CreateIndex");
        Exec(_srcPath, "DROP INDEX idx_users_name;");
    }

    private static async Task<long> CountAsync(string path, string table)
    {
        await using var conn = Open(path);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<object> ScalarAsync(string path, string sql)
    {
        await using var conn = Open(path);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (await cmd.ExecuteScalarAsync())!;
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        return conn;
    }
}
