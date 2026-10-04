using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Acta.AspNetCore;

/// <summary>
/// The whole job screen in one response, composed from the <see cref="IJobs"/> reads after one id lookup: the job row,
/// the size-capped input, result, and checkpoint payloads, explain and lineage, the schedules on its slot, and the
/// eligible workers while it is claimable. A missing part is a null or empty field, not an error, and the two capped
/// collections ship their filter-wide counts. The unbounded event history keeps its own paged route.
/// </summary>
internal sealed record GetJobDetailHttpResponse(
    JobDetail Job,
    JobPayloadHttpResponse Input,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobPayloadHttpResponse? Result,
    IReadOnlyList<CheckpointHttpResponse> Checkpoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobExplanation? Explain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobLineageMap? Lineage,
    IReadOnlyList<ScheduleListItem> Schedules,
    // Filter-wide count, so the frontend can tell a complete set from the first page of a larger one.
    long? SchedulesTotal,
    // Echo of the job row's tenant key at the top level so the summary link needs no nested dig.
    // Absent when the job has no tenant.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantKey,
    // Effective retry budget from the definition so the summary can render "n of m consecutive
    // failures" without a second read. Absent when the definition row is gone.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxAttemptsEffective,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorkerListItem>? Workers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WorkersTotal
)
{
    /// <summary>
    /// The dashboard walked the same child cap; keep it here so the lineage panel is unchanged.
    /// </summary>
    private const int ChildLimit = 100;

    /// <summary>
    /// Compose the detail from the individual reads. The reads are independent but cheap and awaited in
    /// sequence (v1); every read is addressed by the already-resolved id so the whole screen costs one
    /// GetJobIdAsync. Eligible workers are fetched only while the job is claimable, matching the
    /// dashboard's own <c>status === 'ready'</c> gate.
    /// </summary>
    public static async Task<GetJobDetailHttpResponse> ComposeAsync(
        IJobs jobs,
        IActaOperations operations,
        JobDetail job,
        int maxInlineBytes,
        CancellationToken ct
    )
    {
        var byId = JobLookup.ById(job.JobId);

        var input = await jobs.GetInputAsync(byId, ct);
        var result = await jobs.GetResultAsync(byId, ct);
        var checkpoints = await jobs.GetCheckpointsAsync(byId, ct);
        var explain = await jobs.ExplainAsync(byId, ct);
        var lineage = await jobs.GetLineageMapAsync(byId, new JobLineageMapOptions(ChildLimit), ct);
        var schedules = await operations.Schedules.ListAsync(
            new ListSchedulesQuery(
                JobNamespace: job.JobNamespace,
                JobName: job.JobName,
                LiveOnly: false,
                PageSize: 100,
                IncludeTotal: true
            ),
            ct
        );
        var definition = await operations.Definitions.GetAsync(job.JobNamespace, job.JobName, ct);
        // Every worker in the namespace, not just the live ones: the "why isn't this running?" panel
        // needs the whole set to tell "no workers at all" from "workers, none of them active".
        var workers =
            job.Status == JobStatusCode.Ready
                ? await operations.Workers.ListAsync(
                    new ListWorkersQuery(JobNamespace: job.JobNamespace, PageSize: 50, IncludeTotal: true),
                    ct
                )
                : null;

        return new GetJobDetailHttpResponse(
            job,
            JobPayloadHttpResponse.From(input ?? JobPayload.None, maxInlineBytes),
            result is { } produced ? JobPayloadHttpResponse.From(produced, maxInlineBytes) : null,
            (checkpoints?.Items ?? []).Select(item => CheckpointHttpResponse.From(item, maxInlineBytes)).ToList(),
            explain,
            lineage,
            schedules.Items,
            schedules.TotalCount,
            job.TenantKey,
            definition?.MaxAttemptsEffective,
            workers?.Items,
            workers?.TotalCount
        );
    }
}

/// <summary>HTTP projection of one <see cref="CheckpointItem"/>; the value carries the format-dispatched shape.</summary>
internal sealed record CheckpointHttpResponse(
    CheckpointKindCode Kind,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckpointStatusCode? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? DueAtUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JobPayloadHttpResponse? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public static CheckpointHttpResponse From(CheckpointItem item, int maxInlineBytes) =>
        new(
            item.Kind,
            item.Name,
            item.Status,
            item.DueAtUtc,
            item.Value is { } value ? JobPayloadHttpResponse.From(value, maxInlineBytes) : null,
            item.CreatedAtUtc,
            item.ModifiedAtUtc
        );
}

/// <summary>GET /jobs/{jobRef}/detail: the job screen in one request, composed by <see cref="GetJobDetailHttpResponse"/>.</summary>
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

                    var detail = await GetJobDetailHttpResponse.ComposeAsync(
                        jobs,
                        operations,
                        snapshot,
                        jobsOptions.Value.MaxInlinePayloadBytes,
                        ct
                    );
                    return Results.Json(detail, DashboardJsonContext.Default.GetJobDetailHttpResponse);
                }
            )
            .WithSummary("Read the job's full detail aggregate in one request.")
            .Produces<GetJobDetailHttpResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
}
