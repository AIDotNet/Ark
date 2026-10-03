using Ark.Api.Infrastructure;
using Ark.Core.Dtos;
using Ark.Core.Errors;
using Ark.Core.Metadata;

namespace Ark.Api.Endpoints;

public static class MetadataEndpoints
{
    public static IEndpointRouteBuilder MapMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/connections/{id:guid}").WithTags("Metadata");

        g.MapGet("/databases", (Guid id, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), p => p.ListDatabasesAsync(ct), ct));

        g.MapGet("/databases/{db}/schemas", (Guid id, string db, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), p => p.ListSchemasAsync(db, ct), ct));

        g.MapGet("/databases/{db}/schemas/{schema}/tables", (Guid id, string db, string schema, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), p => p.ListTablesAsync(db, schema, ct), ct));

        g.MapGet("/databases/{db}/schemas/{schema}/tables/{table}", async (Guid id, string db, string schema, string table, ArkRepository repo, CancellationToken ct) =>
            await ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                var meta = await p.GetTableAsync(db, schema, table, ct);
                var createSql = await p.GetCreateTableSqlAsync(db, schema, table, ct);
                return new TableDetailResponse(meta, createSql);
            }, ct));

        g.MapGet("/databases/{db}/schemas/{schema}/completion", (Guid id, string db, string schema, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), p => p.GetCompletionSchemaAsync(db, schema, ct), ct));

        return app;
    }
}
