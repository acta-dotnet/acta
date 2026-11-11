namespace Acta;

/// <summary>
/// Header row of the explain read: the job/jobs_state snapshot plus the leasing worker's liveness (via a
/// LEFT JOIN on <c>jobs_state.leased_by_worker_id</c>) and the newest reason on the timeline (the job's latest
/// failed, orphaned, or terminally failed attempt, or a Cancelled job's cancel: its message, actor, and details). Worker and
/// reason columns are null when the Job holds no lease / has no recorded reason. Bound by name from the explain read's
/// row_kind 1 row.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record ExplainJobHeaderRow(
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
    string? LatestReasonMessage,
    string? LatestReasonActor,
    byte? LatestReasonDetailsFormatId,
    byte[]? LatestReasonDetails,
    string? LastExecutedByWorkerVersion,
    Guid? LeasedByWorkerRef,
    Guid? LastExecutedByWorkerRef,
    DefinitionStatusCode DefinitionStatus
);
