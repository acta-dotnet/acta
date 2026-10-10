using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// The whole job screen in one response, composed from the <see cref="IJobs"/> reads after one id lookup: the job row,
/// the size-capped input, result, and checkpoint payloads, explain and lineage, the schedules on its slot, and the
/// eligible workers while it is claimable. A missing part is a null or empty field, not an error, and the two capped
/// collections ship their filter-wide counts. The unbounded event history keeps its own paged route.
/// </summary>
internal sealed record GetJobDetailResponse(
    GetJobResponse Job,
    GetJobInputBodyResponse Input,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GetJobInputBodyResponse? Result,
    IReadOnlyList<GetJobDetailCheckpoint> Checkpoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GetJobExplanationResponse? Explain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GetJobLineageMapResponse? Lineage,
    IReadOnlyList<ListSchedulesItem> Schedules,
    // Filter-wide count, so the frontend can tell a complete set from the first page of a larger one.
    long? SchedulesTotal,
    // Echo of the job row's tenant name at the top level so the summary link needs no nested dig.
    // Absent when the job has no tenant.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TenantName,
    // Effective retry budget from the definition so the summary can render "n of m consecutive
    // failures" without a second read. Absent when the definition row is gone.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxAttemptsEffective,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ListWorkersItem>? Workers,
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
    public static async Task<GetJobDetailResponse> ComposeAsync(
        IJobs jobs,
        IActaOperations operations,
        GetJobResponse job,
        int maxInlineBytes,
        CancellationToken ct
    )
    {
        var byId = JobLookup.ById(job.JobId);

        var input = (await jobs.GetInputAsync(byId, ct))?.Input;
        var result = (await jobs.GetResultAsync(byId, ct))?.Result;
        var checkpoints = await jobs.GetCheckpointsAsync(byId, ct);
        var explain = await jobs.ExplainAsync(byId, ct);
        var lineage = await jobs.GetLineageMapAsync(byId, new GetJobLineageMapOptions(ChildLimit), ct);
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

        return new GetJobDetailResponse(
            job,
            GetJobInputBodyResponse.From(input ?? JobPayload.None, maxInlineBytes),
            result is { } produced ? GetJobInputBodyResponse.From(produced, maxInlineBytes) : null,
            (checkpoints?.Items ?? []).Select(item => GetJobDetailCheckpoint.From(item, maxInlineBytes)).ToList(),
            explain,
            lineage,
            schedules.Items,
            schedules.TotalCount,
            job.TenantName,
            definition?.MaxAttemptsEffective,
            workers?.Items,
            workers?.TotalCount
        );
    }
}
