using Ark.Api.Infrastructure;
using Ark.Core.Dtos;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Providers.Abstractions;

namespace Ark.Api.Endpoints;

public static class ConnectionEndpoints
{
    public static IEndpointRouteBuilder MapConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/connections").WithTags("Connections");

        g.MapGet("/", (ArkRepository repo) => repo.ListConnections().Select(c => c.ToDto()).ToList());

        g.MapGet("/{id:guid}", (Guid id, ArkRepository repo) =>
            repo.GetConnection(id)?.ToDto() ?? throw ArkException.NotFound($"连接 {id} 不存在"));

        g.MapPost("/", (SaveConnectionRequest req, ArkRepository repo, PasswordProtector pw) =>
        {
            req.ValidateForDialect();
            return repo.CreateConnection(req, pw).ToDto();
        });

        g.MapPut("/{id:guid}", (Guid id, SaveConnectionRequest req, ArkRepository repo, PasswordProtector pw) =>
        {
            req.ValidateForDialect();
            return repo.UpdateConnection(id, req, pw).ToDto();
        });

        g.MapDelete("/{id:guid}", (Guid id, ArkRepository repo) => repo.DeleteConnection(id));

        // 测试已保存的连接
        g.MapPost("/{id:guid}/test", async (Guid id, ArkRepository repo, PasswordProtector pw, CancellationToken ct) =>
        {
            var e = repo.GetConnection(id) ?? throw ArkException.NotFound($"连接 {id} 不存在");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var provider = ProviderRegistry.Create(e, pw);
                var version = await provider.TestAsync(ct);
                return new TestConnectionResponse(true, version, sw.Elapsed.TotalMilliseconds, null);
            }
            catch (Exception ex)
            {
                return new TestConnectionResponse(false, null, sw.Elapsed.TotalMilliseconds, ex.Message);
            }
        });

        // 用未保存的表单参数直接测试
        g.MapPost("/test", async (SaveConnectionRequest req, CancellationToken ct) =>
        {
            req.ValidateForDialect();
            var spec = new ConnectionSpec(req.Dialect, req.Host, req.Port, req.Username, req.Password,
                req.Database, req.FilePath, req.ReadOnly, req.Options ?? new Dictionary<string, string>());
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var provider = ProviderRegistry.Create(spec);
                var version = await provider.TestAsync(ct);
                return new TestConnectionResponse(true, version, sw.Elapsed.TotalMilliseconds, null);
            }
            catch (Exception ex)
            {
                return new TestConnectionResponse(false, null, sw.Elapsed.TotalMilliseconds, ex.Message);
            }
        });

        return app;
    }
}
