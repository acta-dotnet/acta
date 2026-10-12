namespace Acta;

/// <summary>
/// One heartbeat of a worker: the executions and lock holds this process holds, the lease window to push them to,
/// whether it is draining, and the recovery interval when this worker takes its namespace's recovery pass (null when it
/// runs no recovery).
/// </summary>
internal sealed record ExtendWorkerLeasesCommand(
    int WorkerId,
    int LeaseTtlSeconds,
    bool Draining,
    IReadOnlyList<(long JobId, int ExecutionNumber)> Executions,
    IReadOnlyList<LockToken> Locks,
    int? RecoveryIntervalSeconds = null
);
