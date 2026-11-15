using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>GET /overview: the ledger's health counters, judged against this deployment's own lease window.</summary>
internal static class GetOverviewEndpoint
{
    public static void Map(RouteGroupBuilder reads) =>
        reads
            .MapGet("/overview", static (HttpContext http, IActaOperations operations, CancellationToken ct) =>
            {
                var query = new GetOverviewQuery(JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"));
                return RouteTable.Run(async () =>
                    Results.Json(await operations.Ledger.GetOverviewAsync(query, ct), ActaApiJsonContext.Relaxed.GetOverviewResponse)
                );
            })
            .WithMetadata(new ActaOperationAttribute("getOverview", ActaPermissions.Read))
            .WithSummary("Read the ledger's health-overview counters.")
            .Produces<GetOverviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Scope the overview to one namespace; omit for all."),
            ]);
}
