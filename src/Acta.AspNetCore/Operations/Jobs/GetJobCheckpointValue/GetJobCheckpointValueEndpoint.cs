using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Acta.AspNetCore;

/// <summary>
/// GET /jobs/{jobRef}/checkpoints/{checkpointKind}/{checkpointName}/value: the value one checkpoint holds as the body itself
/// (<see cref="RawPayload"/>), by the kind and the name the checkpoint list shows it under.
/// </summary>
internal static class GetJobCheckpointValueEndpoint
{
    public static void Map(RouteGroupBuilder reads, ActaEndpointOptions options) =>
        reads
            .MapGet(
                "/jobs/{jobRef}/checkpoints/{checkpointKind}/{checkpointName}/value",
                (HttpContext http, IJobs jobs, CancellationToken ct) =>
                    RouteTable.Run(async () =>
                    {
                        var job = RouteKeys.Job(http, options);
                        var kind = RouteKeys.CheckpointKind(RouteKeys.Value(http, "checkpointKind"));
                        var name = RouteKeys.CheckpointName(RouteKeys.Value(http, "checkpointName"));
                        return await jobs.StoredJobIdAsync(job, ct) is { } jobId
                            ? RawPayload.Serve(http, (await jobs.GetCheckpointValueAsync(JobLookup.ById(jobId), kind, name, ct))?.Value)
                            : RouteTable.NotFound("Job");
                    })
            )
            .WithMetadata(new ActaOperationAttribute("getJobCheckpointValue", ActaPermissions.Read))
            .WithSummary("Read the value one checkpoint of the job holds as the body")
            .ProducesPayload()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
