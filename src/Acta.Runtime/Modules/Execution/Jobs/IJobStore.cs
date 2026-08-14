using System.Data.Common;
using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Persistence port for the Jobs feature: snapshot/status/result/explanation/lineage reads, the
/// keyset job list, and the public-ref and deduplication-key id resolves. Requests arrive validated
/// with identifiers canonicalized and cursors decoded; implementations own command creation,
/// parameter binding, row mapping, and the multi-result-set round trips.
/// </summary>
internal interface IJobStore
{
    /// <summary>The full job snapshot by id, or null when no row matches.</summary>
    ValueTask<JobDetail?> GetJobAsync(long jobId, CancellationToken ct);

    /// <summary>The current status by id, or null when no row matches.</summary>
    ValueTask<JobStatusCode?> GetJobStatusAsync(long jobId, CancellationToken ct);

    /// <summary>The stored input payload and its format for a job, or null when no row matches.</summary>
    Task<JobInputRecord?> GetJobInputAsync(long jobId, CancellationToken ct);

    /// <summary>The stored result payload for the latest (or a specific) execution, or null.</summary>
    Task<JobResultRecord?> GetJobResultAsync(long jobId, int? executionNumber, CancellationToken ct);

    /// <summary>Every checkpoint slot for a job, ordered by kind then name; empty when the job has none.</summary>
    Task<IReadOnlyList<JobCheckpointItem>> GetJobCheckpointsAsync(long jobId, CancellationToken ct);

    /// <summary>The explanation header plus step and checkpoint sets in one round trip, or null.</summary>
    ValueTask<JobExplainData?> GetJobExplanationAsync(long jobId, CancellationToken ct);

    /// <summary>The lineage focus, ancestors, steps, checkpoints, and children in one round trip, or null.</summary>
    ValueTask<JobLineageData?> GetJobLineageMapAsync(long jobId, int childFetchLimit, CancellationToken ct);

    /// <summary>
    /// One keyset page of jobs ordered <c>created_at_utc DESC, id DESC</c> plus an opt-in
    /// filter-wide total, fetched in a single round trip.
    /// </summary>
    Task<JobPage> ListJobsAsync(JobPageRequest request, CancellationToken ct);

    /// <summary>The job id carrying a public ref, falling back to the surviving events ledger after purge.</summary>
    ValueTask<long?> ResolveJobIdByRefAsync(Guid jobRef, CancellationToken ct);

    /// <summary>The job id holding an deduplication key within a namespace.</summary>
    ValueTask<long?> ResolveJobIdByDeduplicationKeyAsync(string jobNamespace, string deduplicationKey, CancellationToken ct);

    /// <summary>One-row enqueue with the pre-allocated public ref; returns exactly one outcome row.</summary>
    Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueOneAsync(JobEnqueueRow row, Guid jobRef, CancellationToken ct);

    /// <summary>
    /// Whole-batch enqueue via the provider-native bulk shape; jobRefs align positionally with rows
    /// and outcomes carry the input ordinal.
    /// </summary>
    Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueBatchAsync(
        IReadOnlyList<JobEnqueueRow> rows,
        IReadOnlyList<Guid> jobRefs,
        CancellationToken ct
    );

    /// <summary>
    /// One-row enqueue executed through the caller's already-started <paramref name="transaction"/>
    /// (its connection is used and left open; Acta neither commits, rolls back, nor retries it).
    /// </summary>
    Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueOneInTransactionAsync(
        DbTransaction transaction,
        JobEnqueueRow row,
        Guid jobRef,
        CancellationToken ct
    );

    /// <summary>
    /// Whole-batch enqueue executed through the caller's already-started <paramref name="transaction"/>;
    /// jobRefs align positionally with rows and outcomes carry the input ordinal.
    /// </summary>
    Task<IReadOnlyList<EnqueueOutcomeRow>> EnqueueBatchInTransactionAsync(
        DbTransaction transaction,
        IReadOnlyList<JobEnqueueRow> rows,
        IReadOnlyList<Guid> jobRefs,
        CancellationToken ct
    );

    /// <summary>Cancels a job; the outcome carries the parent id so the caller can raise the child latch.</summary>
    Task<CancelJobOutcome> CancelJobAsync(long jobId, JobControlInput input, CancellationToken ct);

    /// <summary>Pauses a claimable job.</summary>
    Task<JobControlOutcome> PauseJobAsync(long jobId, JobControlInput input, CancellationToken ct);

    /// <summary>Resumes a paused job, recurring-aware via the caller-resolved next run.</summary>
    Task<JobControlOutcome> ResumeJobAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct);

    /// <summary>Restarts a terminal job, recurring-aware via the caller-resolved next run.</summary>
    Task<JobControlOutcome> RestartJobAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct);

    /// <summary>Moves a scheduled job to a new next-run instant.</summary>
    Task<JobControlOutcome> RescheduleJobAsync(long jobId, DateTime nextRunAtUtc, JobControlInput input, CancellationToken ct);

    /// <summary>Changes a waiting job's priority.</summary>
    Task<JobControlOutcome> ReprioritizeJobAsync(long jobId, JobPriorityCode priority, JobControlInput input, CancellationToken ct);

    /// <summary>Amends a job's stored input; the audit event detail carries the previous payload's format and byte count, never the payload.</summary>
    Task<JobControlOutcome> UpdateJobInputAsync(long jobId, JobPayload input, JobControlInput controlInput, CancellationToken ct);

    /// <summary>Hard-deletes a terminal job; the surviving events carry the public ref.</summary>
    Task<JobControlOutcome> PurgeJobAsync(long jobId, JobControlInput input, CancellationToken ct);

    /// <summary>Clears a job's durable step/checkpoint state ahead of a fresh attempt.</summary>
    Task ResetJobStateAsync(long jobId, CancellationToken ct);
}

/// <summary>Validated, cursor-decoded request for one job page; Take carries the peek-ahead row.</summary>
internal sealed record JobPageRequest(
    string? JobNamespace,
    JobStatusCode? Status,
    string? JobName,
    long? ParentJobId,
    int? TenantId,
    string? TenantKey,
    string? CorrelationKey,
    string? TagFiltersJson,
    bool? TerminalOnly,
    bool? RecurringOnly,
    DateTime? CursorCreatedAtUtc,
    long? CursorId,
    int Take,
    bool IncludeTotal
);

/// <summary>One page of mapped job list items plus the opt-in filtered total.</summary>
internal sealed record JobPage(IReadOnlyList<JobListItem> Rows, long? Total);
