namespace Acta;

/// <summary>
/// Header row of the explain read: the job/runtime snapshot plus the leasing worker's liveness (via a
/// LEFT JOIN on <c>runtimes.leased_by_worker_id</c>) and the latest reason recorded on the timeline
/// (the most recent <c>events</c> row carrying a reason, joined by <c>MAX(id)</c>). Columns are in
/// SELECT order; worker and reason columns are null when the Job holds no lease / has no reasoned event.
/// </summary>
[DbProjection]
internal sealed record GetJobExplanationHeaderRow(
    long JobId,
    Guid JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    int FailureCount,
    int MaxAttemptsEffective,
    DateTime? NextRunAtUtc,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    string? WorkerDeploymentVersion,
    WorkerStatusCode? WorkerStatus,
    DateTime? WorkerLastSeenAtUtc,
    EventReasonCode? LatestReasonCode,
    string? LatestReasonMessage,
    string? LastExecutedByWorkerName,
    Guid? LeasedByWorkerRef,
    Guid? LastExecutedByWorkerRef
);
