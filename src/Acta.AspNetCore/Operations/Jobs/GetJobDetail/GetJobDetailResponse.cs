using System.Text.Json.Serialization;

namespace Acta.AspNetCore;

/// <summary>
/// Everything the job page shows, in one answer: the job, its input and result, the first page of its checkpoints, its explanation and
/// lineage, the schedules that fire it, and the workers that can claim it. A part that does not apply is absent or empty, and each capped
/// list comes with its total.
/// </summary>
/// <param name="Job">The job.</param>
/// <param name="Input">The job's input as a payload envelope; absent when it has none.</param>
/// <param name="Result">The job's result as a payload envelope; absent until the job has one.</param>
/// <param name="Checkpoints">The first page of the job's checkpoints, with their total.</param>
/// <param name="Explain">The explanation of the job's current state.</param>
/// <param name="Lineage">The job's lineage map.</param>
/// <param name="Schedules">The first page of the schedules of the job's definition; empty when it has none.</param>
/// <param name="SchedulesTotal">How many schedules the definition has, so a caller can tell a complete list from a first page.</param>
/// <param name="MaxAttemptsEffective">The retry budget in force for the job's definition; absent when the definition is gone.</param>
/// <param name="Workers">The first page of the namespace's workers while the job is claimable; absent otherwise.</param>
/// <param name="WorkersTotal">How many such workers there are; absent with workers.</param>
internal sealed record GetJobDetailResponse(
    GetJobResponse Job,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StoredJobPayload? Input,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StoredJobPayload? Result,
    ListJobCheckpointsResponse Checkpoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExplainJobResponse? Explain,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GetJobLineageMapResponse? Lineage,
    IReadOnlyList<ListSchedulesItem> Schedules,
    long? SchedulesTotal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxAttemptsEffective,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ListWorkersItem>? Workers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? WorkersTotal
)
{
    /// <summary>
    /// The dashboard walked the same child cap; keep it here so the lineage panel is unchanged.
    /// </summary>
    private const int ChildLimit = 100;

    /// <summary>The definition's schedules the detail carries; the schedules list pages the rest.</summary>
    internal const int SchedulePageSize = 100;

    /// <summary>The namespace's workers the detail carries while the job is claimable; the workers list pages the rest.</summary>
    internal const int WorkerPageSize = 50;

    /// <summary>
    /// Compose the detail from the individual reads. The reads are independent but cheap and awaited in
    /// sequence (v1); every read is addressed by the already-resolved id so the whole screen costs one
    /// GetIdAsync. Eligible workers are fetched only while the job is claimable, matching the
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
        var checkpoints = await jobs.ListCheckpointsAsync(new ListJobCheckpointsQuery(byId, IncludeTotal: true), ct);
        var explain = await jobs.ExplainAsync(byId, ct);
        var lineage = await jobs.GetLineageMapAsync(byId, new GetJobLineageMapOptions(ChildLimit), ct);
        var schedules = await operations.Schedules.ListAsync(
            new ListSchedulesQuery(
                JobNamespace: job.JobNamespace,
                JobName: job.JobName,
                LiveOnly: false,
                PageSize: SchedulePageSize,
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
                    new ListWorkersQuery(JobNamespace: job.JobNamespace, PageSize: WorkerPageSize, IncludeTotal: true),
                    ct
                )
                : null;

        return new GetJobDetailResponse(
            job,
            StoredJobPayload.From(input, maxInlineBytes),
            StoredJobPayload.From(result, maxInlineBytes),
            ListJobCheckpointsResponse.From(checkpoints, maxInlineBytes),
            explain,
            lineage,
            schedules.Items,
            schedules.TotalCount,
            definition?.MaxAttemptsEffective,
            workers?.Items,
            workers?.TotalCount
        );
    }
}
