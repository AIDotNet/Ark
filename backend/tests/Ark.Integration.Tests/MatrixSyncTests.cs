using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;
using Ark.Providers.ClickHouse;
using Ark.Providers.DM8;
using Ark.Providers.DuckDB;
using Ark.Providers.KingbaseES;
using Ark.Providers.MariaDB;
using Ark.Providers.MySQL;
using Ark.Providers.OpenGauss;
using Ark.Providers.PostgreSQL;
using Ark.Providers.SQLite;
using Ark.Providers.SqlServer;
using Ark.Sync;
using DotNet.Testcontainers.Builders;
using Xunit;

namespace Ark.Integration.Tests;

/// <summary>
/// Phase 2 全组合同步矩阵：任意源 → 任意目标（结构转换 + 全量复制 + 行级 Diff）。
/// - 默认（CI 冒烟）：仅跑含 PostgreSQL / MySQL 的配对（12 对）；
/// - ARK_MATRIX=1：七方言全组合（7×6 = 42 有序对）。
/// 容器每方言启动一次全矩阵复用；DuckDB 为本地临时文件无需容器；
/// 达梦 DM8 / 人大金仓镜像不公开分发，不在矩阵内（见 MariaDbSqlServerSyncTests / KesOpenGaussDm8SyncTests 的 env 门控用例）。
/// </summary>
public sealed class MatrixPoolFixture : IAsyncLifetime
{
    private readonly Dictionary<ArkDialect, MatrixDb> _pool = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly string DuckPath =
        Path.Combine(Path.GetTempPath(), $"ark_mx_{Environment.ProcessId}_{Guid.NewGuid():N}.duckdb");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var db in _pool.Values)
        {
            try { await db.Shutdown(); } catch { /* 尽力清理 */ }
        }
        if (File.Exists(DuckPath)) File.Delete(DuckPath);
    }

    public async Task<MatrixDb> GetAsync(ArkDialect dialect)
    {
        await _gate.WaitAsync();
        try
        {
            if (_pool.TryGetValue(dialect, out var cached)) return cached;
            var db = await StartAsync(dialect);
            _pool[dialect] = db;
            return db;
        }
        finally { _gate.Release(); }
    }

    private static async Task StartOrSkip(Func<Task> start)
    {
        try { await start(); }
        catch (Exception ex) when (MatrixSyncTests.IsDockerNetworkError(ex))
        {
            throw new SkipException($"容器/镜像不可用，跳过：{ex.Message}");
        }
    }

    private static async Task<MatrixDb> StartAsync(ArkDialect d)
    {
        switch (d)
        {
            case ArkDialect.DuckDB:
                return new MatrixDb(d, "", 0, "", "", "main", DuckPath, () => Task.CompletedTask);

            case ArkDialect.PostgreSQL:
            {
                var c = new Testcontainers.PostgreSql.PostgreSqlBuilder()
                    .WithImage("postgres:16-alpine")
                    .WithDatabase("ark_mx").WithUsername("ark").WithPassword("ark123")
                    .Build();
                await StartOrSkip(() => c.StartAsync());
                return new MatrixDb(d, c.Hostname, c.GetMappedPublicPort(5432), "ark", "ark123", "ark_mx", null,
                    () => c.DisposeAsync().AsTask());
            }
            case ArkDialect.MySQL:
            {
                var c = new Testcontainers.MySql.MySqlBuilder()
                    .WithImage("mysql:8.4")
                    .WithDatabase("ark_mx").WithUsername("root").WithPassword("ark123")
                    .Build();
                await StartOrSkip(() => c.StartAsync());
                return new MatrixDb(d, c.Hostname, c.GetMappedPublicPort(3306), "root", "ark123", "ark_mx", null,
                    () => c.DisposeAsync().AsTask());
            }
            case ArkDialect.MariaDB:
            {
                var c = new Testcontainers.MariaDb.MariaDbBuilder()
                    .WithImage("mariadb:11.4")
                    .WithDatabase("ark_mx").WithUsername("ark").WithPassword("ark123")
                    .Build();
                await StartOrSkip(() => c.StartAsync());
                return new MatrixDb(d, c.Hostname, c.GetMappedPublicPort(3306), "ark", "ark123", "ark_mx", null,
                    () => c.DisposeAsync().AsTask());
            }
            case ArkDialect.SqlServer:
            {
                var c = new Testcontainers.MsSql.MsSqlBuilder().Build(); // 默认镜像 + 默认 sa 密码
                await StartOrSkip(() => c.StartAsync());
                return new MatrixDb(d, c.Hostname, c.GetMappedPublicPort(1433), "sa",
                    Testcontainers.MsSql.MsSqlBuilder.DefaultPassword, "master", null,
                    () => c.DisposeAsync().AsTask());
            }
            case ArkDialect.ClickHouse:
            {
                var c = new Testcontainers.ClickHouse.ClickHouseBuilder()
                    .WithImage("clickhouse/clickhouse-server:24.8")
                    .WithDatabase("default").WithUsername("ark").WithPassword("ark123")
                    .Build();
                await StartOrSkip(() => c.StartAsync());
                return new MatrixDb(d, c.Hostname, c.GetMappedPublicPort(8123), "ark", "ark123", "default", null,
                    () => c.DisposeAsync().AsTask());
            }
            case ArkDialect.OpenGauss:
            {
                var c = new ContainerBuilder()
                    .WithImage("opengauss/opengauss:5.0.0")
                    .WithEnvironment("GS_PASSWORD", "Ark@12345")
                    .WithPortBinding(5432, true)
                    .Build();
                await StartOrSkip(() => c.StartAsync());
                var info = new MatrixDb(d, c.Hostname, c.GetMappedPublicPort(5432), "gaussdb", "Ark@12345", "postgres", null,
                    () => c.DisposeAsync().AsTask());
                await WaitReadyOrSkipAsync(info); // openGauss 初始化会重启 postgres：端口开放 ≠ 服务就绪
                return info;
            }
            default:
                throw new SkipException($"方言 {d} 不在矩阵范围内（DM8/金仓镜像不公开，见独立 env 门控用例）");
        }
    }

    /// <summary>容器启动后轮询 TestAsync 至服务真正就绪（openGauss 初始化重启 / 认证不兼容 → Skip），最长 120s。</summary>
    private static async Task WaitReadyOrSkipAsync(MatrixDb info)
    {
        await using var provider = info.CreateProvider();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await provider.TestAsync();
                return;
            }
            catch (Exception ex) when (attempt < 24 && MatrixSyncTests.IsTransientStartupError(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                throw new SkipException($"方言 {info.Dialect} 容器就绪检查失败，跳过：{ex.Message}");
            }
        }
    }
}

/// <summary>矩阵中一个方言的连接信息与 Provider 工厂。</summary>
public sealed record MatrixDb(
    ArkDialect Dialect, string Host, int Port, string User, string Pass, string Database, string? FilePath,
    Func<Task> Shutdown)
{
    public IDbProvider CreateProvider()
    {
        var spec = new ConnectionSpec(Dialect, Host, Port, User, Pass, Database, FilePath, false,
            new Dictionary<string, string>());
        return Dialect switch
        {
            ArkDialect.PostgreSQL => new PostgreSqlProvider(spec),
            ArkDialect.MySQL => new MySqlProvider(spec),
            ArkDialect.MariaDB => new MariaDbProvider(spec),
            ArkDialect.SqlServer => new SqlServerProvider(spec),
            ArkDialect.ClickHouse => new ClickHouseProvider(spec),
            ArkDialect.DuckDB => new DuckDbProvider(spec),
            ArkDialect.OpenGauss => new OpenGaussProvider(spec),
            _ => throw new ArgumentOutOfRangeException(nameof(Dialect), Dialect, "矩阵外方言"),
        };
    }
}

[CollectionDefinition("matrix")]
public sealed class MatrixCollection : ICollectionFixture<MatrixPoolFixture>;

[Collection("matrix")]
public class MatrixSyncTests(MatrixPoolFixture pool)
{
    private const string Tbl = "ark_matrix";

    public static IEnumerable<object[]> PairData()
    {
        ArkDialect[] all =
        [
            ArkDialect.PostgreSQL, ArkDialect.MySQL, ArkDialect.MariaDB,
            ArkDialect.SqlServer, ArkDialect.ClickHouse, ArkDialect.OpenGauss, ArkDialect.DuckDB,
        ];
        // ARK_MATRIX_DIALECTS=PostgreSQL,MySQL,... 可临时收缩方言集（如镜像未就绪时）
        var whitelist = Environment.GetEnvironmentVariable("ARK_MATRIX_DIALECTS");
        if (!string.IsNullOrWhiteSpace(whitelist))
        {
            var allowed = whitelist.Split(',').Select(x => Enum.TryParse<ArkDialect>(x.Trim(), out var d) ? d : (ArkDialect?)null)
                .Where(d => d is not null).Select(d => d!.Value).ToHashSet();
            all = all.Where(allowed.Contains).ToArray();
        }
        var matrix = Environment.GetEnvironmentVariable("ARK_MATRIX") == "1";
        foreach (var s in all)
        foreach (var t in all)
        {
            if (s == t) continue;
            // 冒烟子集：至少一端是 PostgreSQL / MySQL（三库主干）
            if (!matrix && s is not (ArkDialect.PostgreSQL or ArkDialect.MySQL)
                       && t is not (ArkDialect.PostgreSQL or ArkDialect.MySQL)) continue;
            yield return [s, t];
        }
    }

    private static bool DockerAvailable()
    {
        if (Environment.GetEnvironmentVariable("ARK_INTEGRATION") == "1") return true;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("docker", "info --format ok")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            _ = p ?? throw new InvalidOperationException();
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    internal static bool IsTransientStartupError(Exception ex)
    {
        var msg = ex.ToString();
        if (msg.Contains("AuthenticationSASL", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("0 mechanisms", StringComparison.OrdinalIgnoreCase)) return false; // 认证不兼容：不重试
        return ex is System.Data.Common.DbException or IOException or System.Net.Sockets.SocketException
            || msg.Contains("starting up", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("the database system is starting", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsDockerNetworkError(Exception ex) =>
        ex is HttpRequestException or IOException
        || ex.Message.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("pull access denied", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("EOF", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase);

    /// <summary>矩阵用规范模型：int 主键 + 变长字符串 + 定标小数 + 布尔 + 可空备注。</summary>
    private static CanonicalTable Model() => new()
    {
        Name = Tbl,
        Columns =
        [
            new CanonicalColumn { Name = "id", Type = CanonicalType.Of(CanonicalTypeId.Int32), Nullable = false },
            new CanonicalColumn { Name = "name", Type = new CanonicalType(CanonicalTypeId.VarChar, 100), Nullable = false },
            new CanonicalColumn { Name = "price", Type = new CanonicalType(CanonicalTypeId.Decimal, 10, 2) },
            new CanonicalColumn { Name = "flag", Type = CanonicalType.Bool, Nullable = false },
            new CanonicalColumn { Name = "note", Type = new CanonicalType(CanonicalTypeId.VarChar, 200) },
        ],
        PrimaryKeyColumns = ["id"],
    };

    private static List<object?[]> Seed() =>
    [
        [1, "alice", 10.50m, true, "first"],
        [2, "bob", 20.00m, false, null],
        [3, "carol", 30.25m, true, "third"],
        [4, "dave", 40.10m, false, null],
        [5, "erin", 50.99m, true, "fifth"],
    ];

    private static string? SchemaFor(ArkDialect d)
    {
        var desc = Dialects.Get(d);
        return desc.SupportsSchemas ? desc.DefaultTargetSchema : null;
    }

    private static SyncPlanRequest Opts(string srcDb, string tgtDb, TableSelection sel) => new()
    {
        SourceConnectionId = Guid.NewGuid(),
        SourceDatabase = srcDb,
        TargetConnectionId = Guid.NewGuid(),
        TargetDatabase = tgtDb,
        Mode = SyncMode.StructureAndData,
        DataMethod = DataSyncMethod.FullCopy,
        ConflictMode = ConflictMode.Truncate,
        BatchSize = 2, // 强制多窗口，压测 keyset 翻页
        Tables = [sel],
    };

    private static async Task DropQuietAsync(IDbProvider p, TableRef t)
    {
        try { await p.ExecuteDdlAsync(t.Database, p.Ddl.DropTable(t)); }
        catch { /* 表不存在等：忽略 */ }
    }

    private static async Task<List<string>> ReadCanonicalAsync(IDbProvider p, TableRef t, CanonicalTable meta)
    {
        var page = await p.ReadRowsAsync(t, meta, new RowQueryRequest { Limit = 100 });
        var cols = meta.Columns.Select(c => c.Name).ToList();
        var idx = cols.ToDictionary(c => c, c => cols.FindIndex(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)));
        return page.Rows
            .OrderBy(r => Convert.ToInt64(r[0]))
            .Select(r => string.Join("\x1F", cols.Select(c => CellValue.CanonicalString(r[idx[c]]))))
            .ToList();
    }

    [SkippableTheory]
    [MemberData(nameof(PairData))]
    public async Task Structure_FullCopy_RowDiff_Matrix(ArkDialect s, ArkDialect t)
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过矩阵配对");
        var srcInfo = await pool.GetAsync(s);
        var tgtInfo = await pool.GetAsync(t);
        await using var sp = srcInfo.CreateProvider();
        await using var tp = tgtInfo.CreateProvider();

        var srcSchema = SchemaFor(s);
        var tgtSchema = Dialects.Get(t).ResolveTargetSchema(s, srcSchema);
        var srcRef = new TableRef(srcInfo.Database, srcSchema, Tbl);
        var tgtRef = new TableRef(tgtInfo.Database, tgtSchema, Tbl);
        var sel = new TableSelection { Database = tgtInfo.Database, Schema = tgtSchema, Table = Tbl };
        var opts = Opts(srcInfo.Database, tgtInfo.Database, sel);

        try
        {
            await DropQuietAsync(sp, srcRef);
            await DropQuietAsync(tp, tgtRef);

            // ① 结构：规范模型经源端 DDL 生成器建表 + 种子数据（RowOps 参数化）
            var model = Model();
            await sp.ExecuteDdlAsync(srcInfo.Database, sp.Ddl.CreateTable(srcRef, model, []));
            await using (var conn = await sp.OpenConnectionAsync(srcInfo.Database))
            {
                await RowOps.InsertRowsAsync(conn, null, sp, srcRef,
                    model.Columns.Select(c => c.Name).ToList(), model.Columns, Seed(), ConflictMode.Error, CancellationToken.None);
            }

            // ② 跨方言转换 + 目标建表
            var srcMeta = await sp.GetTableAsync(srcInfo.Database, srcSchema, Tbl);
            var (converted, issues) = TableConverter.Convert(srcMeta, s, t);
            Assert.DoesNotContain(issues, i => i.Severity == "error");
            await tp.ExecuteDdlAsync(tgtInfo.Database, tp.Ddl.CreateTable(tgtRef, converted, []));
            var tgtMeta = await tp.GetTableAsync(tgtInfo.Database, tgtSchema, Tbl);
            Assert.Equal(["id"], tgtMeta.PrimaryKeyColumns);

            // ③ 全量复制（批量通道）+ 逐行 canonical 比对
            var engine = new DataTransferEngine();
            var stats = await engine.FullCopyAsync(sp, tp, srcRef, srcMeta, tgtRef, tgtMeta, sel, opts);
            Assert.Equal(5, stats.Inserted);
            await AssertRowsEqualAsync(s, t, sp, srcRef, srcMeta, tp, tgtRef, tgtMeta);

            // ④ 行级 Diff：源端改 1 / 删 1 / 增 1（RowOps 方言感知；CH 的 mutation 异步生效需轮询）→ apply → 双侧再比对
            var cols = model.Columns.Select(c => c.Name).ToList();
            await using (var conn = await sp.OpenConnectionAsync(srcInfo.Database))
            {
                await RowOps.UpdateRowAsync(conn, null, sp, srcRef, ["id"], cols, model.Columns,
                    [2, "bob-upd", 20.00m, false, null], CancellationToken.None);
                await RowOps.DeleteRowsAsync(conn, null, sp, srcRef, ["id"], [[3]], CancellationToken.None);
                await RowOps.InsertRowsAsync(conn, null, sp, srcRef, cols, model.Columns,
                    [[6, "frank", 60.60m, false, null]], ConflictMode.Error, CancellationToken.None);
            }
            if (s == ArkDialect.ClickHouse)
                await PollSourceVisibleAsync(sp, srcRef, 5);
            var diff = await engine.DiffTableAsync(sp, tp, srcRef, srcMeta, tgtRef, tgtMeta, sel, opts with
            {
                DataMethod = DataSyncMethod.RowDiff,
                ConflictMode = ConflictMode.Error,
                DeleteExtraRows = true, // 行级 Diff 应用目标端多余行的删除
                ChunkRows = 2,          // 强制多窗口 keyset 翻页
            });
            if (t != ArkDialect.ClickHouse) // CH mutation 返回 0 受影响行，计数无意义
            {
                Assert.Equal(1, diff.Updated);
                Assert.Equal(1, diff.Deleted);
                Assert.Equal(1, diff.Inserted);
            }
            await AssertRowsEqualAsync(s, t, sp, srcRef, srcMeta, tp, tgtRef, tgtMeta);
        }
        finally
        {
            await DropQuietAsync(sp, srcRef);
            await DropQuietAsync(tp, tgtRef);
        }
    }

    /// <summary>双侧行集 canonical 比对；ClickHouse 目标为异步 mutation，轮询等待生效（最长 15s）。</summary>
    private static async Task AssertRowsEqualAsync(
        ArkDialect s, ArkDialect t, IDbProvider sp, TableRef srcRef, CanonicalTable srcMeta,
        IDbProvider tp, TableRef tgtRef, CanonicalTable tgtMeta)
    {
        var expected = await ReadCanonicalAsync(sp, srcRef, srcMeta);
        if (t != ArkDialect.ClickHouse)
        {
            Assert.Equal(expected, await ReadCanonicalAsync(tp, tgtRef, tgtMeta));
            return;
        }
        for (var attempt = 0; ; attempt++)
        {
            var actual = await ReadCanonicalAsync(tp, tgtRef, tgtMeta);
            if (expected.SequenceEqual(actual)) return;
            if (attempt >= 29)
            {
                Assert.Equal(expected, actual); // 最终仍不一致 → 带差异失败
                return;
            }
            await Task.Delay(500);
        }
    }

    /// <summary>CH 源端 mutation 异步生效：轮询直到行数/内容可见（最长 15s），否则后续 Diff 读到旧快照造成假失败。</summary>
    private static async Task PollSourceVisibleAsync(IDbProvider p, TableRef t, int expectedCount)
    {
        await using var conn = await p.OpenConnectionAsync(t.Database);
        for (var attempt = 0; ; attempt++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT name FROM {p.Ddl.TableName(t)} WHERE {p.Ddl.Quote("id")} = 2";
            var name = await cmd.ExecuteScalarAsync();
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = $"SELECT COUNT(*) FROM {p.Ddl.TableName(t)}";
            var count = Convert.ToInt64(await cmd2.ExecuteScalarAsync());
            if (Equals(name, "bob-upd") && count == expectedCount) return;
            if (attempt >= 29) Assert.Fail($"ClickHouse 源端 mutation 未在期限内可见（name={name}, count={count}）");
            await Task.Delay(500);
        }
    }
}
