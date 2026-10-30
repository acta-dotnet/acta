namespace Acta;

/// <summary>
/// Header row of the explain read: the job/jobs_state snapshot plus the leasing worker's liveness (via a
/// LEFT JOIN on <c>jobs_state.leased_by_worker_id</c>) and the newest failure on the timeline (the job's latest
/// failed, orphaned, or terminally failed attempt: its message and details). Columns are in SELECT order; worker
/// and failure columns are null when the Job holds no lease / has no recorded failure.
/// </summary>
[DbProjection]
internal sealed record GetJobExplanationHeaderRow(
    long JobId,
    Guid JobRef,
    string JobNamespace,
    string JobName,
    JobKindCode Kind,
    JobStatusCode Status,
    int FailureCount,
    int MaxAttemptsEffective,
    DateTime? NextRunAtUtc,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    string? WorkerDeploymentVersion,
    WorkerStatusCode? WorkerStatus,
    DateTime? WorkerLastSeenAtUtc,
    string? LatestFailureMessage,
    byte? LatestFailureDetailsFormatId,
    byte[]? LatestFailureDetails,
    string? LastExecutedByWorkerName,
    Guid? LeasedByWorkerRef,
    Guid? LastExecutedByWorkerRef
);
