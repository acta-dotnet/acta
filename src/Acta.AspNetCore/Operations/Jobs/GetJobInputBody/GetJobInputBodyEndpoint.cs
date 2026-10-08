using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// GET /jobs/{jobRef}/input: the stored input on its own, rendered by <see cref="GetJobInputBodyResponse"/>, for callers
/// that want only the payload (the enqueue screen's clone prefill) and would otherwise pay the whole detail composition
/// for one field.
/// </summary>
internal static class GetJobInputBodyEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapGet(
                "/jobs/{jobRef}/input",
                async Task<IResult> (string jobRef, IJobs jobs, IOptions<JobsOptions> jobsOptions, CancellationToken ct) =>
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

                    var input = (await jobs.GetInputAsync(JobLookup.ById(snapshot.JobId), ct))?.Input;
                    return Results.Json(
                        GetJobInputBodyResponse.From(input ?? JobPayload.None, jobsOptions.Value.MaxInlinePayloadBytes),
                        DashboardJsonContext.Default.GetJobInputBodyResponse
                    );
                }
            )
            .WithSummary("Read the job's stored input payload.")
            .Produces<GetJobInputBodyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
}
