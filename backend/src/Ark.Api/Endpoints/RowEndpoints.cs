using Ark.Api.Infrastructure;
using Ark.Core.Errors;
using Ark.Core.Metadata;
using Ark.Core.Querying;

namespace Ark.Api.Endpoints;

public static class RowEndpoints
{
    public static IEndpointRouteBuilder MapRowEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/connections/{id:guid}/databases/{db}/schemas/{schema}/tables/{table}/rows")
            .WithTags("Rows");

        g.MapPost("/query", (Guid id, string db, string schema, string table, RowQueryRequest req, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                Identifier.EnsureValid(table, "表名");
                var meta = await p.GetTableAsync(db, schema, table, ct);
                return await p.ReadRowsAsync(new TableRef(db, schema, table), meta, req, ct);
            }, ct));

        g.MapPost("/changes", (Guid id, string db, string schema, string table, RowChangeSet req, ArkRepository repo, CancellationToken ct) =>
            ProviderRequest.UseAsync(id, repo, repo.Pw(), async p =>
            {
                Identifier.EnsureValid(table, "表名");
                var meta = await p.GetTableAsync(db, schema, table, ct);
                return await p.ApplyChangesAsync(new TableRef(db, schema, table), meta, req, ct);
            }, ct));

        return app;
    }
}
