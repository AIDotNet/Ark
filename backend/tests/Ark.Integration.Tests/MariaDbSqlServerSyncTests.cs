using Ark.Core.Metadata;
using Ark.Core.Querying;
using Ark.Core.Typing;
using Ark.Providers.Abstractions;
using Ark.Sync;
using Xunit;

namespace Ark.Integration.Tests;

/// <summary>
/// MariaDB / SQL Server Provider 集成测试（Testcontainers：mariadb:11.4 + mssql 2022）。
/// 本机无 Docker 时自动跳过（ARK_INTEGRATION=1 强制要求运行）。
/// 流程：建源表 + 插入 → Provider 取元数据 → TableConverter.Convert 到目标方言 → 断言转换警告/类型。
/// </summary>
public class MariaDbSqlServerSyncTests
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

    [SkippableFact]
    public async Task MariaDb_Provider_Metadata_Converts_To_SqlServer()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");

        var container = new Testcontainers.MariaDb.MariaDbBuilder()
            .WithImage("mariadb:11.4")
            .WithDatabase("ark_demo").WithUsername("ark").WithPassword("ark123")
            .Build();
        try
        {
            await StartOrSkipAsync(() => container.StartAsync());
        }
        catch (SkipException)
        {
            return;
        }
        try
        {
            var port = (int)container.GetMappedPublicPort(3306);
            ExecMaria(container.Hostname, port, """
                CREATE TABLE users (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    name VARCHAR(100) NOT NULL,
                    balance DECIMAL(12,2) NULL,
                    is_active TINYINT(1) NOT NULL DEFAULT 1,
                    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    payload LONGTEXT NULL
                );
                CREATE INDEX idx_users_name ON users (name);
                INSERT INTO users (name, balance, payload) VALUES ('alice', 1.50, '{}'), ('bob', NULL, NULL);
                """);

            await using var provider = new Ark.Providers.MariaDB.MariaDbProvider(
                new ConnectionSpec(ArkDialect.MariaDB, container.Hostname, port, "ark", "ark123", "ark_demo",
                    null, false, new Dictionary<string, string>()));

            // 连接测试 + 系统库过滤
            var version = await provider.TestAsync();
            Assert.StartsWith("MariaDB", version);
            var dbs = await provider.ListDatabasesAsync();
            Assert.Contains(dbs, d => d.Name == "ark_demo");
            Assert.DoesNotContain(dbs, d => d.Name == "mysql");
            Assert.DoesNotContain(dbs, d => d.Name == "information_schema");

            // 元数据
            var meta = await provider.GetTableAsync("ark_demo", null, "users");
            Assert.False(meta.IsView);
            Assert.Equal(["id"], meta.PrimaryKeyColumns);
            var id = Assert.Single(meta.Columns, c => c.Name == "id");
            Assert.True(id.IsAutoIncrement);
            Assert.True(id.IsPrimaryKey);
            Assert.Equal(CanonicalTypeId.Int32, id.Type.Id);
            var balance = Assert.Single(meta.Columns, c => c.Name == "balance");
            Assert.Equal(CanonicalTypeId.Decimal, balance.Type.Id);
            Assert.Equal(12, balance.Type.Precision);
            var isActive = Assert.Single(meta.Columns, c => c.Name == "is_active");
            Assert.Equal(CanonicalTypeId.Boolean, isActive.Type.Id); // tinyint(1) → Bool
            var createdAt = Assert.Single(meta.Columns, c => c.Name == "created_at");
            Assert.Equal(ValueExprKind.CurrentTimestamp, createdAt.DefaultKind);
            var payload = Assert.Single(meta.Columns, c => c.Name == "payload");
            Assert.Equal(CanonicalTypeId.Text, payload.Type.Id); // LONGTEXT（JSON 列实际类型）
            Assert.Contains(meta.Indexes, i => i.Name == "idx_users_name");

            // 行读取（LIMIT/OFFSET 共享实现）
            var page = await provider.ReadRowsAsync(
                new TableRef("ark_demo", null, "users"), meta, new RowQueryRequest { Limit = 1, Offset = 1 });
            Assert.Equal(2, page.Total);
            var row = Assert.Single(page.Rows);
            Assert.Equal("bob", row[1]);

            // 转换到 SQL Server：类型/默认值语义保持，无 error 级问题
            var (converted, issues) = TableConverter.Convert(meta, ArkDialect.MariaDB, ArkDialect.SqlServer);
            Assert.DoesNotContain(issues, i => i.Severity == "error");
            var convName = Assert.Single(converted.Columns, c => c.Name == "name");
            Assert.Equal("nvarchar(100)", TypeMapper.FromCanonical(convName.Type, ArkDialect.SqlServer).Sql);
            var convCreated = Assert.Single(converted.Columns, c => c.Name == "created_at");
            Assert.Equal(CanonicalTypeId.DateTime, convCreated.Type.Id);
            Assert.Equal("SYSDATETIME()", convCreated.DefaultValueSql, ignoreCase: true);

            // 目标方言语法由 SQL Server DDL 生成器表达：IDENTITY(1,1) / bit
            var warnings = new List<string>();
            var ddl = string.Join("\n",
                new Ark.Providers.SqlServer.SqlServerDdlGenerator().CreateTable(converted, warnings));
            Assert.Contains("IDENTITY(1,1)", ddl);
            Assert.Contains("[users]", ddl);
            Assert.Contains("bit", ddl, StringComparison.OrdinalIgnoreCase);

            // MariaDB 侧生成器：AUTO_INCREMENT + utf8mb4
            var myWarnings = new List<string>();
            var myDdl = string.Join("\n", provider.Ddl.CreateTable(meta, myWarnings));
            Assert.Contains("AUTO_INCREMENT", myDdl);
            Assert.Contains("utf8mb4", myDdl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task SqlServer_Provider_Metadata_Converts_To_MariaDb()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");

        var container = new Testcontainers.MsSql.MsSqlBuilder()
            .Build(); // 默认镜像 mcr.microsoft.com/mssql/server:2022-latest，默认 sa 密码满足复杂度要求
        try
        {
            await StartOrSkipAsync(() => container.StartAsync());
        }
        catch (SkipException)
        {
            return;
        }
        try
        {
            var port = (int)container.GetMappedPublicPort(1433);
            var saPassword = Testcontainers.MsSql.MsSqlBuilder.DefaultPassword;
            ExecMsSql(container.Hostname, port, saPassword, """
                CREATE TABLE dbo.users (
                    id INT IDENTITY(1,1) PRIMARY KEY,
                    name NVARCHAR(100) NOT NULL,
                    uid UNIQUEIDENTIFIER NULL,
                    is_active BIT NOT NULL DEFAULT 0,
                    created_at DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
                    big_text NVARCHAR(MAX) NULL
                );
                CREATE INDEX idx_users_name ON dbo.users (name);
                INSERT INTO dbo.users (name, uid, is_active, big_text) VALUES
                    ('alice', NEWID(), 1, N'{}'), ('bob', NULL, 0, NULL);
                """);

            await using var provider = new Ark.Providers.SqlServer.SqlServerProvider(
                new ConnectionSpec(ArkDialect.SqlServer, container.Hostname, port, "sa", saPassword,
                    "master", null, false, new Dictionary<string, string>()));

            // 连接测试 + schema/表列表（系统 schema 过滤）
            var version = await provider.TestAsync();
            Assert.Contains("SQL Server", version);
            var schemas = await provider.ListSchemasAsync("master");
            Assert.Contains(schemas, s => s.Name == "dbo");
            Assert.DoesNotContain(schemas, s => s.Name == "db_owner");
            Assert.DoesNotContain(schemas, s => s.Name == "INFORMATION_SCHEMA");
            var tables = await provider.ListTablesAsync("master", "dbo");
            Assert.Contains(tables, t => t.Name == "users" && t.Kind == "table");

            // 元数据
            var meta = await provider.GetTableAsync("master", "dbo", "users");
            Assert.False(meta.IsView);
            Assert.Equal(["id"], meta.PrimaryKeyColumns);
            var id = Assert.Single(meta.Columns, c => c.Name == "id");
            Assert.True(id.IsAutoIncrement); // IDENTITY → IsAutoIncrement
            Assert.True(id.IsPrimaryKey);
            var uid = Assert.Single(meta.Columns, c => c.Name == "uid");
            Assert.Equal(CanonicalTypeId.Guid, uid.Type.Id); // uniqueidentifier → Guid
            var isActive = Assert.Single(meta.Columns, c => c.Name == "is_active");
            Assert.Equal(CanonicalTypeId.Boolean, isActive.Type.Id); // bit → Bool
            Assert.Equal(ValueExprKind.Literal, isActive.DefaultKind); // ((0)) → 字面量
            var createdAt = Assert.Single(meta.Columns, c => c.Name == "created_at");
            Assert.Equal(CanonicalTypeId.DateTime, createdAt.Type.Id); // datetime2 → DateTime
            Assert.Equal(ValueExprKind.CurrentTimestamp, createdAt.DefaultKind); // SYSDATETIME() 识别
            var bigText = Assert.Single(meta.Columns, c => c.Name == "big_text");
            Assert.Equal(CanonicalTypeId.Text, bigText.Type.Id); // nvarchar(max) → Text
            Assert.Contains(meta.Indexes, i => i.Name == "idx_users_name");

            // 行数估算（sys.dm_db_partition_stats）
            Assert.Equal(2, await provider.EstimateRowsAsync(new TableRef("master", "dbo", "users")));

            // 行读取：分页 override（OFFSET/FETCH + 默认主键排序）
            var page1 = await provider.ReadRowsAsync(
                new TableRef("master", "dbo", "users"), meta, new RowQueryRequest { Limit = 1, Offset = 0 });
            Assert.Equal(2, page1.Total);
            Assert.Single(page1.Rows);
            var page2 = await provider.ReadRowsAsync(
                new TableRef("master", "dbo", "users"), meta, new RowQueryRequest { Limit = 1, Offset = 1 });
            var row2 = Assert.Single(page2.Rows);
            Assert.Equal("bob", row2[1]);

            // 转换到 MariaDB：类型映射 + 警告（bit → tinyint(1)）
            var (converted, issues) = TableConverter.Convert(meta, ArkDialect.SqlServer, ArkDialect.MariaDB);
            Assert.DoesNotContain(issues, i => i.Severity == "error");
            Assert.Contains(issues, i => i.Severity == "warning" && i.Message.Contains("is_active"));
            var convUid = Assert.Single(converted.Columns, c => c.Name == "uid");
            Assert.Equal("char(36)", TypeMapper.FromCanonical(convUid.Type, ArkDialect.MariaDB).Sql);
            var convName = Assert.Single(converted.Columns, c => c.Name == "name");
            Assert.Equal("varchar(100)", TypeMapper.FromCanonical(convName.Type, ArkDialect.MariaDB).Sql);

            // SQL Server 侧生成器：限定名 / 默认值 / 外键 / 自增重置
            var gen = new Ark.Providers.SqlServer.SqlServerDdlGenerator();
            var warnings = new List<string>();
            var ddl = string.Join("\n", gen.CreateTable(new TableRef("master", "dbo", "users"), converted, warnings));
            Assert.Contains("[dbo].[users]", ddl);
            Assert.Contains("IDENTITY(1,1)", ddl);
            Assert.Contains("DEFAULT SYSDATETIME()", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("DBCC CHECKIDENT ('[dbo].[users]', RESEED, 100);",
                gen.ResetAutoIncrement(new TableRef("master", "dbo", "users"), "id", 100).Single());
            var fk = new CanonicalForeignKey
            {
                Name = "fk_users_dept",
                Columns = ["dept_id"],
                ReferencedTable = "dbo.depts",
                ReferencedColumns = ["id"],
                OnDelete = "CASCADE",
            };
            Assert.Contains("REFERENCES [dbo].[depts] ([id]) ON DELETE CASCADE",
                gen.CreateForeignKey(fk, new TableRef("master", "dbo", "users")).Single());
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>启动容器；镜像拉取失败（如无法访问 Docker Hub）→ 抛 SkipException 跳过。</summary>
    private static async Task StartOrSkipAsync(Func<Task> start)
    {
        try
        {
            await start();
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
               || msg.Contains("mcr.microsoft.com", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("docker.io", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("failed to resolve reference", StringComparison.OrdinalIgnoreCase)
               || (msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                   && msg.Contains("docker", StringComparison.OrdinalIgnoreCase));
    }

    private static void ExecMaria(string host, int port, string sql)
    {
        using var conn = new MySqlConnector.MySqlConnection(new MySqlConnector.MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            UserID = "ark",
            Password = "ark123",
            Database = "ark_demo",
            AllowPublicKeyRetrieval = true,
        }.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ExecMsSql(string host, int port, string password, string sql)
    {
        // 容器自签证书：必须 Encrypt=Optional，否则 TLS 握手失败
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(
            $"Server={host},{port};Database=master;User Id=sa;Password={password};" +
            "Encrypt=Optional;TrustServerCertificate=True");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
