using Ark.Core.Metadata;
using Ark.Core.Sync;
using Ark.Sync;
using Xunit;

namespace Ark.Sync.Tests;

public class ExtensionInstallTests
{
    private static ExtensionDependency Missing(string name, string sql, string? note = null) =>
        new() { Name = name, InstallSql = sql, Present = false, Note = note };

    [Fact]
    public void Prepend_MissingExtension_AsFirstAction()
    {
        var actions = new List<DdlAction>
        {
            new() { Id = "t:SetSearchPath:x", Kind = "SetSearchPath", Summary = "切换 search_path", Sql = "SET search_path TO app;" },
            new() { Id = "t:CreateTable:x", Kind = "CreateTable", Summary = "创建表 t", Sql = "CREATE TABLE t();" },
        };
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        SyncPlanner.PrependExtensionActions(
            actions, [Missing("pgcrypto", "CREATE EXTENSION IF NOT EXISTS pgcrypto;", "由 pgcrypto 提供")], "t", emitted);

        var install = Assert.Single(actions.Where(a => a.Kind == "InstallExtension"));
        Assert.Equal(0, actions.IndexOf(install)); // 置顶：先于 SetSearchPath 与建表（CREATE TABLE 时即解析默认值函数）
        Assert.Equal("CREATE EXTENSION IF NOT EXISTS pgcrypto;", install.Sql);
        Assert.False(install.IsDestructive);
        Assert.Equal(["由 pgcrypto 提供"], install.Warnings);
    }

    [Fact]
    public void Dedupes_AcrossTables()
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var sql = "CREATE EXTENSION IF NOT EXISTS pgcrypto;";
        var first = new List<DdlAction>();
        var second = new List<DdlAction>();

        SyncPlanner.PrependExtensionActions(first, [Missing("pgcrypto", sql)], "a", emitted);
        SyncPlanner.PrependExtensionActions(second, [Missing("pgcrypto", sql)], "b", emitted);

        Assert.Single(first.Where(a => a.Kind == "InstallExtension"));
        Assert.Empty(second.Where(a => a.Kind == "InstallExtension")); // 跨表只发一次
    }

    [Fact]
    public void Skips_Present_And_NotInstallable()
    {
        var actions = new List<DdlAction>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        SyncPlanner.PrependExtensionActions(actions,
        [
            new ExtensionDependency { Name = "gen_random_uuid", InstallSql = "", Present = true, Note = "内置" },
            new ExtensionDependency { Name = "some_ext", InstallSql = "", Present = false, Note = "目标端不可用" },
        ], "t", emitted);

        Assert.Empty(actions); // 已存在/不可安装的都不发射
        Assert.Empty(emitted);
    }

    // ---------- 全量扩展清单同步（SyncExtensions） ----------

    [Theory]
    [InlineData(ArkDialect.PostgreSQL, ArkDialect.PostgreSQL, true)]
    [InlineData(ArkDialect.PostgreSQL, ArkDialect.KingbaseES, true)]
    [InlineData(ArkDialect.OpenGauss, ArkDialect.PostgreSQL, true)]
    [InlineData(ArkDialect.PostgreSQL, ArkDialect.MySQL, false)]
    [InlineData(ArkDialect.MySQL, ArkDialect.PostgreSQL, false)]
    [InlineData(ArkDialect.SQLite, ArkDialect.SQLite, false)]
    public void SupportsExtensionInventorySync_OnlyBetweenPgFamily(ArkDialect source, ArkDialect target, bool expected)
        => Assert.Equal(expected, SyncPlanner.SupportsExtensionInventorySync(source, target));
}
