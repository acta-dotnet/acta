namespace Acta;

/// <summary>
/// One heartbeat of a worker: the executions and lock holds this process holds, the lease window to push them to, and
/// whether it is draining.
/// </summary>
internal sealed record ExtendWorkerLeasesCommand(
    int WorkerId,
    int LeaseTtlSeconds,
    bool Draining,
    IReadOnlyList<(long JobId, int ExecutionNumber)> Executions,
    IReadOnlyList<LockToken> Locks
);
