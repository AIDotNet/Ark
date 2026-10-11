using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Responses;
using Ark.Providers.Abstractions;
using Ark.Providers.DM8;
using Ark.Providers.KingbaseES;
using Ark.Providers.OpenGauss;
using DotNet.Testcontainers.Builders;
using Xunit;

namespace Ark.Integration.Tests;

/// <summary>
/// 人大金仓 / openGauss / 达梦 DM8 容器连接与元数据冒烟测试。
/// - openGauss：公共镜像 opengauss/opengauss:5.0.0；自研 sha256 认证与标准 Npgsql 不兼容时自动跳过。
/// - 人大金仓：镜像不在公共仓库，取环境变量 ARK_KES_IMAGE，未设置则跳过；仅验证「非 PG 兼容模式 → 明确模式错误」。
/// - 达梦：镜像不在公共仓库，取环境变量 ARK_DM8_IMAGE，未设置则跳过。
/// 本机无 Docker 时全部自动跳过（ARK_INTEGRATION=1 强制要求运行）。
/// </summary>
public class KesOpenGaussDm8SyncTests
{
    private static readonly IReadOnlyDictionary<string, string> EmptyOptions = new Dictionary<string, string>();

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

    // ---------------------------------------------------------------- openGauss

    [SkippableFact]
    public async Task OpenGauss_Container_TestConnection_AndMetadataSmoke()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");

        var container = new ContainerBuilder()
            .WithImage("opengauss/opengauss:5.0.0")
            .WithEnvironment("GS_PASSWORD", "Ark@12345")
            .WithPortBinding(5432, true)
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
            await using var target = new OpenGaussProvider(new ConnectionSpec(
                ArkDialect.OpenGauss, container.Hostname, container.GetMappedPublicPort(5432),
                "gaussdb", "Ark@12345", "postgres", null, false, EmptyOptions));

            string version;
            try
            {
                version = await TestWithStartupRetryAsync(target);
            }
            catch (Exception ex) when (IsAuthIncompatible(ex))
            {
                // openGauss 自研 sha256 认证机制与标准 Npgsql 不兼容 → 跳过（留给官方 Npgsql 分支验证）
                throw new SkipException($"openGauss 认证不兼容（sha256/password authentication），跳过：{Compact(ex.Message)}");
            }
            Assert.False(string.IsNullOrWhiteSpace(version));

            // 元数据冒烟：单库列表 + schema 列表
            var dbs = await target.ListDatabasesAsync();
            Assert.Contains(dbs, d => d.Name == "postgres");
            var schemas = await target.ListSchemasAsync("postgres");
            Assert.NotEmpty(schemas);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- 人大金仓

    [SkippableFact]
    public async Task KingbaseEs_Container_ModeGuard_ReportsPgCompatibilityError()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");
        var image = Environment.GetEnvironmentVariable("ARK_KES_IMAGE");
        Skip.If(string.IsNullOrWhiteSpace(image), "未设置 ARK_KES_IMAGE（人大金仓镜像不在公共仓库），跳过");

        var container = new ContainerBuilder()
            .WithImage(image!)
            .WithPortBinding(54321, true)
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
            // 凭据可按机器覆盖（ARK_KES_USER / ARK_KES_PASSWORD）；缺省用 system 超级用户
            var user = Environment.GetEnvironmentVariable("ARK_KES_USER") ?? "system";
            var password = Environment.GetEnvironmentVariable("ARK_KES_PASSWORD") ?? "";
            await using var target = new KdbndpProvider(new ConnectionSpec(
                ArkDialect.KingbaseES, container.Hostname, container.GetMappedPublicPort(54321),
                user, password, null, null, false, EmptyOptions));

            try
            {
                var version = await TestWithStartupRetryAsync(target);
                // 连接成功路径（镜像已切 PG 兼容模式的机器）：版本串非空即通过
                Assert.False(string.IsNullOrWhiteSpace(version));
            }
            catch (ArkException ex) when (ex.Code == ArkErrorCodes.ConnectionConfigInvalid
                                          && ex.Message.Contains("PG 兼容模式"))
            {
                // 预期路径：容器默认 oracle/mysql 等模式 → 明确给出模式错误（本测试唯一目标）
            }
            catch (Exception ex) when (IsAuthError(ex))
            {
                // 容器内 system 用户凭据/初始化参数无法自动设置 → 跳过，连接成功路径留给有镜像的机器
                throw new SkipException($"金仓容器认证失败（可用 ARK_KES_USER/ARK_KES_PASSWORD 覆盖），跳过：{Compact(ex.Message)}");
            }
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- 达梦 DM8

    [SkippableFact]
    public async Task Dm8_Container_TestConnection_AndSingleDatabase()
    {
        Skip.IfNot(DockerAvailable(), "Docker 不可用，跳过 Testcontainers 集成测试");
        var image = Environment.GetEnvironmentVariable("ARK_DM8_IMAGE");
        Skip.If(string.IsNullOrWhiteSpace(image), "未设置 ARK_DM8_IMAGE（达梦镜像不在公共仓库），跳过");

        var container = new ContainerBuilder()
            .WithImage(image!)
            .WithPortBinding(5236, true)
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
            // 默认 SYSDBA + 镜像缺省密码；可按机器用 ARK_DM8_PASSWORD 覆盖
            var password = Environment.GetEnvironmentVariable("ARK_DM8_PASSWORD") ?? "SYSDBA001";
            await using var target = new Dm8Provider(new ConnectionSpec(
                ArkDialect.DM8, container.Hostname, container.GetMappedPublicPort(5236),
                "SYSDBA", password, null, null, false, EmptyOptions));

            string version;
            try
            {
                version = await TestWithStartupRetryAsync(target);
            }
            catch (Exception ex) when (IsAuthError(ex))
            {
                throw new SkipException($"达梦容器认证失败（可用 ARK_DM8_PASSWORD 覆盖），跳过：{Compact(ex.Message)}");
            }
            Assert.False(string.IsNullOrWhiteSpace(version));

            // DM 单库：V$DATABASE 单行
            var dbs = await target.ListDatabasesAsync();
            Assert.Single(dbs);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>启动容器；镜像拉取失败（如无法访问 Docker Hub / 内网仓库）→ 抛 SkipException 跳过。</summary>
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
               || msg.Contains("failed to resolve reference", StringComparison.OrdinalIgnoreCase)
               || (msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
                   && msg.Contains("docker", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Testcontainers 只等端口开放：openGauss 初始化会重启 postgres（连接重置/拒绝），重试至多 120s。</summary>
    private static async Task<string> TestWithStartupRetryAsync(IDbProvider provider)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await provider.TestAsync();
            }
            catch (Exception ex) when (attempt < 24 && IsTransientStartupError(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    /// <summary>启动期瞬态错误：初始化重启导致的连接重置/拒绝/"starting up"；认证不兼容不算瞬态（立即跳过）。</summary>
    private static bool IsTransientStartupError(Exception ex)
    {
        if (IsAuthIncompatible(ex)) return false;
        var msg = ex.ToString();
        return ex is System.Data.Common.DbException or IOException or System.Net.Sockets.SocketException
            || msg.Contains("starting up", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("the database system is starting", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>openGauss 自研 sha256 认证与标准 Npgsql 不兼容的判定关键字。</summary>
    private static bool IsAuthIncompatible(Exception ex)
    {
        var msg = ex.Message + " " + (ex.InnerException?.Message ?? "");
        return msg.Contains("sha256", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("password authentication", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("AuthenticationSASL", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("0 mechanisms", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthError(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("password authentication", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("login denied", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("用户名或密码", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("28P01", StringComparison.OrdinalIgnoreCase);
    }

    private static string Compact(string message)
    {
        var s = message.Replace('\n', ' ');
        return s.Length <= 300 ? s : s[..300] + "…";
    }
}
