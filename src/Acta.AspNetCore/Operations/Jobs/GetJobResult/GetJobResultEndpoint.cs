using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>GET /jobs/{jobRef}/result: the result the job stored as the body itself (<see cref="RawPayload"/>).</summary>
internal static class GetJobResultEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapGet("/jobs/{jobRef}/result", (HttpContext http, IJobs jobs, CancellationToken ct) =>
                RouteTable.Run(async () =>
                    await jobs.StoredJobIdAsync(RouteKeys.Job(http, options), ct) is { } jobId
                        ? RawPayload.Serve(http, (await jobs.GetResultAsync(JobLookup.ById(jobId), ct))?.Result)
                        : RouteTable.NotFound("Job")
                )
            )
            .WithMetadata(new ActaOperationAttribute("getJobResult", ActaPermissions.Read))
            .WithSummary("Read the result the job stored as the body.")
            .ProducesPayload()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
