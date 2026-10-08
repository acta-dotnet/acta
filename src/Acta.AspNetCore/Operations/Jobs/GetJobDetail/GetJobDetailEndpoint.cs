using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>GET /jobs/{jobRef}/detail: the job screen in one request, composed by <see cref="GetJobDetailResponse"/>.</summary>
internal static class GetJobDetailEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapGet(
                "/jobs/{jobRef}/detail",
                async Task<IResult> (
                    string jobRef,
                    IJobs jobs,
                    IActaOperations operations,
                    IOptions<JobsOptions> jobsOptions,
                    CancellationToken ct
                ) =>
                {
                    if (!JobTargetBinding.TryParseTarget(jobRef, options, out var lookup))
                    {
                        return RefSegment.Malformed("jobRef", "job");
                    }

                    var snapshot = await jobs.GetAsync(lookup, ct);
                    if (snapshot is null)
                    {
                        return RouteTable.NotFound("Job");
                    }

                    var detail = await GetJobDetailResponse.ComposeAsync(
                        jobs,
                        operations,
                        snapshot,
                        jobsOptions.Value.MaxInlinePayloadBytes,
                        ct
                    );
                    return Results.Json(detail, DashboardJsonContext.Default.GetJobDetailResponse);
                }
            )
            .WithSummary("Read the job's full detail aggregate in one request.")
            .Produces<GetJobDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
}
