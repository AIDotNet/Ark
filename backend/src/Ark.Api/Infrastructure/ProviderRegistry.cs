using System.Text.Json;
using Ark.Core.Dtos;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Providers.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Ark.Api.Infrastructure;

public sealed record ConnectionEntity(
    Guid Id,
    string Name,
    ArkDialect Dialect,
    string? Host,
    int? Port,
    string? Username,
    string? PasswordEnc,
    string? Database,
    string? FilePath,
    bool ReadOnly,
    string OptionsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public Dictionary<string, string> Options() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(OptionsJson) ?? [];

    public ConnectionDto ToDto() => new(Id, Name, Dialect, Host, Port, Username,
        PasswordEnc is not null, Database, FilePath, ReadOnly, Options(), CreatedAt, UpdatedAt);

    public ConnectionSpec ToSpec(PasswordProtector pw) => new(
        Dialect, Host, Port, Username, pw.Unprotect(PasswordEnc), Database, FilePath, ReadOnly, Options());
}

/// <summary>连接密码保护器（ASP.NET Data Protection，密钥落盘 data/keys）。</summary>
public sealed class PasswordProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Ark.ConnectionPasswords");

    public string Protect(string plain) => _protector.Protect(plain);
    public string? Unprotect(string? cipher) => cipher is null ? null : _protector.Unprotect(cipher);
}

/// <summary>三库 Provider 工厂。</summary>
public static class ProviderRegistry
{
    public static IDbProvider Create(ConnectionSpec spec) => spec.Dialect switch
    {
        ArkDialect.PostgreSQL => new Ark.Providers.PostgreSQL.PostgreSqlProvider(spec),
        ArkDialect.MySQL => new Ark.Providers.MySQL.MySqlProvider(spec),
        ArkDialect.SQLite => new Ark.Providers.SQLite.SqliteProvider(spec),
        _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Dialect, "未知方言"),
    };

    public static IDbProvider Create(ConnectionEntity e, PasswordProtector pw) => Create(e.ToSpec(pw));
}

/// <summary>「按连接 id 打开 provider 执行一段逻辑」的统一入口。</summary>
public static class ProviderRequest
{
    public static async Task<T> UseAsync<T>(
        Guid connectionId, ArkRepository repo, PasswordProtector pw,
        Func<IDbProvider, Task<T>> fn, CancellationToken ct = default)
    {
        var e = repo.GetConnection(connectionId) ?? throw ArkException.NotFound($"连接 {connectionId} 不存在");
        await using var provider = ProviderRegistry.Create(e, pw);
        return await fn(provider);
    }
}
