using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// GET /jobs/{jobRef}/input: the stored input as the body itself (<see cref="RawPayload"/>), for a caller that wants only
/// the payload, as the enqueue screen's clone does, and would otherwise pay the whole detail composition for one field.
/// </summary>
internal static class GetJobInputEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapGet("/jobs/{jobRef}/input", (HttpContext http, IJobs jobs, CancellationToken ct) =>
                RouteTable.Run(async () =>
                    await jobs.StoredJobIdAsync(RouteKeys.Job(http, options), ct) is { } jobId
                        ? RawPayload.Serve(http, (await jobs.GetInputAsync(JobLookup.ById(jobId), ct))?.Input)
                        : RouteTable.NotFound("Job")
                )
            )
            .WithMetadata(new ActaOperationAttribute("getJobInput", ActaPermissions.Read))
            .WithSummary("Read the job's stored input as the body.")
            .ProducesPayload()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
