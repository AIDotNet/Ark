using Ark.Api.Infrastructure;
using Ark.Core.Dtos;
using Ark.Core.Querying;

namespace Ark.Api.Endpoints;

public static class QueryEndpoints
{
    public static IEndpointRouteBuilder MapQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/connections/{id:guid}").WithTags("Query");

        g.MapPost("/query", (Guid id, ExecuteSqlRequest req, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(),
                p => p.ExecuteQueryAsync(req.Database, req.Sql, req.MaxRows, req.TimeoutSeconds, ct), ct));

        g.MapPost("/explain", (Guid id, ExplainRequest req, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(),
                p => p.ExplainAsync(req.Database, req.Sql, req.TimeoutSeconds, ct), ct));

        return app;
    }
}
