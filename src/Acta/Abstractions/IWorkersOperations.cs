namespace Acta;

/// <summary>
/// <see cref="IWorkers"/> plus the worker lifecycle writes the engine runs: the bootstrap, the clean stop, the heartbeat
/// lease refresh, and the dead-worker sweep.
/// </summary>
internal interface IWorkersOperations : IWorkers
{
    /// <summary>
    /// Single bootstrap round trip: updates the runtime's <c>namespaces</c> row only when its catalog
    /// hash changed and inserts it only when the name is absent, appends a fresh <c>workers</c> row for
    /// this process, and records a <c>worker.started</c> event, returning both DB-assigned ids. The
    /// worker id is the owner stamp on every claim / lease / execution write.
    /// </summary>
    Task<StartWorkerResponse> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct);

    /// <summary>
    /// Clean-shutdown counterpart to start: flips this process's <c>workers</c> row from
    /// Active/Draining to Stopped and records a <c>worker.stopped</c> event. A no-op when the worker
    /// is already terminal (a hard kill leaves the row Active for the dead sweep to reap).
    /// </summary>
    Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct);

    /// <summary>
    /// Heartbeat lease refresh for one worker: stamps <c>workers.last_seen_at_utc</c> and pushes forward by one TTL
    /// window each lock hold whose token still holds its key and the lease of each held execution the worker still
    /// leases. Answers the renewed holds and those executions, each flagged renewed or skipped because another
    /// transaction held the row; a hold or held execution not answered is gone, and a leased row nothing holds lapses.
    /// Also answers the namespace's newest <c>definitions.modified_at_utc</c>. Does not bump <c>runtimes.version</c>.
    /// </summary>
    Task<ExtendWorkerLeasesResponse> ExtendWorkerLeasesAsync(
        int workerId,
        int leaseTtlSeconds,
        bool draining,
        IReadOnlyList<(long JobId, int ExecutionNumber)> executions,
        IReadOnlyList<LockToken> locks,
        CancellationToken ct
    );

    /// <summary>
    /// Dead-worker sweep across all namespaces: flips Active <c>workers</c> rows whose
    /// <c>last_seen_at_utc</c> is older than the window to Dead, skipping rows another sweep holds, and
    /// emits one <c>worker.died</c> event per reaped worker. The count marked is diagnostic.
    /// </summary>
    Task<MarkDeadWorkersResponse> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct);
}
