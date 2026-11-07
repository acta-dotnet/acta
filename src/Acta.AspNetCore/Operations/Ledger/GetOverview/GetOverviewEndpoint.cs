using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>GET /overview: the ledger's health counters, judged against this deployment's own lease window.</summary>
internal static class GetOverviewEndpoint
{
    public static void Map(RouteGroupBuilder reads) =>
        reads
            .MapGet(
                "/overview",
                static (HttpContext http, IActaOperations operations, IOptions<JobsOptions> jobs, CancellationToken ct) =>
                {
                    // Staleness and executor capacity key on this deployment's own lease window rather than
                    // the query default: past the lease a worker's jobs are already reclaimable, so its
                    // executors are not capacity. A host that widens the lease widens both readings with it.
                    var query = new GetOverviewQuery(
                        JobNamespace: QueryBinding.Text(http.Request.Query, "jobNamespace"),
                        StaleWorkerAfterSeconds: jobs.Value.LeaseTtlSeconds
                    );
                    return RouteTable.Run(async () =>
                        Results.Json(await operations.Ledger.GetOverviewAsync(query, ct), ActaApiJsonContext.Default.GetOverviewResponse)
                    );
                }
            )
            .WithMetadata(new ActaOperationAttribute("getOverview", ActaPermissions.Read))
            .WithSummary("Read the ledger's health-overview counters.")
            .Produces<GetOverviewResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithQueryParameters([
                new QueryParameterDoc("jobNamespace", QueryParameterKind.String, "Scope the overview to one namespace; omit for all."),
            ]);
}
