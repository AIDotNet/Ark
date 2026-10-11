using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Sync;
using Ark.Providers.Abstractions;
using Ark.Providers.ClickHouse;
using Xunit;

namespace Ark.Integration.Tests;

/// <summary>
/// ClickHouse 集成测试（Testcontainers：clickhouse/clickhouse-server:24.8）。
/// 无 Docker 时自动跳过（ARK_INTEGRATION=1 强制要求运行）。
/// 覆盖：连接测试、建表（MergeTree ORDER BY）、可空类型解包、排序键→主键、批量写入、行读。
/// </summary>
public class ClickHouseSyncTests
{
    private static bool DockerAvailable()
    {
        if (Environment.GetEnvironmentVariable("ARK_INTEGRATION") == "1") return true;
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("docker", "info --format ok")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
            _ = p ?? throw new InvalidOperationException();
            var ok = p.WaitForExit(5000) && p.ExitCode == 0;
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private ClickHouseProvider Provider(Testcontainers.ClickHouse.ClickHouseContainer container) =>
        new(new ConnectionSpec(ArkDialect.ClickHouse, container.Hostname,
            (int)container.GetMappedPublicPort(8123), "ark", "ark123", "ark_test", null, false,
            new Dictionary<string, string>()));

    private static async Task<Testcontainers.ClickHouse.ClickHouseContainer> StartContainerAsync()
    {
        var ch = new Testcontainers.ClickHouse.ClickHouseBuilder()
            .WithImage("clickhouse/clickhouse-server:24.8")
            .WithDatabase("ark_test").WithUsername("ark").WithPassword("ark123")
            .Build();
        await ch.StartAsync();
        return ch;
    }

    [SkippableFact]
    public async Task TestAsync_And_Metadata_ExposeStructure()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");
        var container = await StartOrSkipAsync(StartContainerAsync);
        try
        {
            await using var target = Provider(container);

            // 连接测试：SELECT version()
            var version = await target.TestAsync();
            Assert.Contains("24.8", version);

            // 库列表：排除 system/INFORMATION_SCHEMA
            var dbs = await target.ListDatabasesAsync();
            Assert.Contains(dbs, d => d.Name == "ark_test");
            Assert.DoesNotContain(dbs, d => d.Name == "system");

            // schema 层固定 default
            Assert.Equal(["default"], (await target.ListSchemasAsync("ark_test")).Select(s => s.Name));

            // 建表：可空列包 Nullable(...)，主键落 MergeTree ORDER BY
            var model = new CanonicalTable
            {
                Name = "events",
                Columns =
                [
                    new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
                    new CanonicalColumn { Name = "name", Type = CanonicalType.Str, Nullable = true },
                    new CanonicalColumn { Name = "score", Type = CanonicalType.Of(CanonicalTypeId.Double), Nullable = true },
                    new CanonicalColumn { Name = "created_at", Type = CanonicalType.Of(CanonicalTypeId.DateTime), Nullable = false },
                ],
                PrimaryKeyColumns = ["id"],
            };
            var warnings = new List<string>();
            var tgtRef = new TableRef("ark_test", null, "events");
            var stmts = target.Ddl.CreateTable(tgtRef, model, warnings);
            Assert.DoesNotContain(warnings, w => w.Contains("ORDER BY")); // 有主键 → 无 tuple() 警告
            await target.ExecuteDdlAsync("ark_test", stmts);

            // 回读元数据：Nullable(String) 解包 → 可空 Text；sorting_key → 主键
            var meta = await target.GetTableAsync("ark_test", null, "events");
            Assert.Equal(["id"], meta.PrimaryKeyColumns);
            Assert.False(meta.Columns.Single(c => c.Name == "id").Nullable);
            var nameCol = meta.Columns.Single(c => c.Name == "name");
            Assert.Equal(CanonicalTypeId.Text, nameCol.Type.Id);
            Assert.True(nameCol.Nullable);

            // 行数估算走 system.tables.total_rows
            Assert.Equal(0, await target.EstimateRowsAsync(tgtRef));
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task BulkWriter_And_RowRead_TransferRows()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");
        var container = await StartOrSkipAsync(StartContainerAsync);
        try
        {
            await using var target = Provider(container);

            var model = new CanonicalTable
            {
                Name = "rows",
                Columns =
                [
                    new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
                    new CanonicalColumn { Name = "label", Type = CanonicalType.Str, Nullable = false },
                ],
                PrimaryKeyColumns = ["id"],
            };
            var tgtRef = new TableRef("ark_test", null, "rows");
            await target.ExecuteDdlAsync("ark_test", target.Ddl.CreateTable(tgtRef, model, new List<string>()));

            // 批量通道：ClickHouseBulkCopy (RowBinary)
            await using (var writer = await target.OpenBulkWriterAsync(tgtRef, ["id", "label"], model.Columns))
            {
                Assert.Equal("ClickHouseBulkCopy (RowBinary)", writer.Channel);
                for (var i = 1; i <= 5; i++)
                    await writer.WriteAsync([(long)i, "row-" + i]);
                await writer.CompleteAsync();
                Assert.Equal(5, writer.RowsWritten);
            }

            // 无主键表 → ORDER BY tuple() + 警告
            var noPk = new CanonicalTable
            {
                Name = "no_pk",
                Columns = [new CanonicalColumn { Name = "v", Type = CanonicalType.Str, Nullable = false }],
            };
            var noPkWarnings = new List<string>();
            target.Ddl.CreateTable(new TableRef("ark_test", null, "no_pk"), noPk, noPkWarnings);
            Assert.Contains(noPkWarnings, w => w.Contains("ORDER BY"));

            // 读行：LIMIT/OFFSET 分页（ProviderBase 共享实现）
            var meta = await target.GetTableAsync("ark_test", null, "rows");
            var page = await target.ReadRowsAsync(tgtRef, meta, new Ark.Core.Querying.RowQueryRequest { Limit = 3, Offset = 1 });
            Assert.Equal(5, page.Total);
            Assert.Equal(3, page.Rows.Count);

            // 变更集：无事务逐条执行（ALTER TABLE UPDATE/DELETE mutation）
            var changes = new RowChangeSet
            {
                Inserts = [new RowInsert(new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["id"] = System.Text.Json.JsonSerializer.SerializeToElement(99),
                    ["label"] = System.Text.Json.JsonSerializer.SerializeToElement("inserted"),
                })],
            };
            var applied = await target.ApplyChangesAsync(tgtRef, meta, changes);
            Assert.Equal(1, applied.Inserted);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task DdlGenerator_EmitsMergeTreeAndModifyColumn()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");
        var container = await StartOrSkipAsync(StartContainerAsync);
        try
        {
            await using var target = Provider(container);
            var model = new CanonicalTable
            {
                Name = "ddl_check",
                Columns =
                [
                    new CanonicalColumn { Name = "id", Type = CanonicalType.Long, Nullable = false },
                    new CanonicalColumn { Name = "extra", Type = CanonicalType.Str, Nullable = true },
                ],
                PrimaryKeyColumns = ["id"],
            };
            var tgtRef = new TableRef("ark_test", null, "ddl_check");
            await target.ExecuteDdlAsync("ark_test", target.Ddl.CreateTable(tgtRef, model, new List<string>()));

            // ALTER：加列 / 改列类型（MODIFY COLUMN）/ 删列
            var add = new CanonicalColumn { Name = "score", Type = CanonicalType.Of(CanonicalTypeId.Double), Nullable = true };
            await target.ExecuteDdlAsync("ark_test",
                target.Ddl.AlterTable(new TableDiff { Target = tgtRef, After = model, AddedColumns = [add] }, new List<string>()));
            await target.ExecuteDdlAsync("ark_test",
                target.Ddl.AlterTable(new TableDiff
                {
                    Target = tgtRef,
                    After = model,
                    AlteredColumns = [new ColumnChange(add, new CanonicalColumn { Name = "score", Type = CanonicalType.Str, Nullable = true })],
                }, new List<string>()));

            // 索引/外键：ClickHouse 无 → 返回空（由计划警告）
            Assert.Empty(target.Ddl.CreateIndex(new CanonicalIndex { Name = "i", Columns = ["id"] }, tgtRef));
            Assert.Empty(target.Ddl.DropIndex("i", tgtRef));
            // TRUNCATE 支持
            Assert.NotEmpty(target.Ddl.TruncateTable(tgtRef));
            Assert.Empty(target.Ddl.ResetAutoIncrement(tgtRef, "id", 10));

            var meta = await target.GetTableAsync("ark_test", null, "ddl_check");
            Assert.Equal(3, meta.Columns.Count);
            Assert.Equal(CanonicalTypeId.Text, meta.Columns.Single(c => c.Name == "score").Type.Id);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>启动容器；镜像拉取失败（如无法访问 Docker Hub）→ 抛 SkipException 跳过。</summary>
    private static async Task<Testcontainers.ClickHouse.ClickHouseContainer> StartOrSkipAsync(
        Func<Task<Testcontainers.ClickHouse.ClickHouseContainer>> start)
    {
        try
        {
            return await start();
        }
        catch (Exception ex) when (IsDockerNetworkError(ex))
        {
            throw new SkipException($"无法拉取镜像（网络受限）：{ex.Message}");
        }
    }

    private static bool IsDockerNetworkError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("registry-1.docker.io", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("failed to resolve reference", StringComparison.OrdinalIgnoreCase)
            || (msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase) && msg.Contains("docker", StringComparison.OrdinalIgnoreCase));
    }
}
