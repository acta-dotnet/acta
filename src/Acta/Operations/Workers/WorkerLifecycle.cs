namespace Acta;

/// <summary>
/// The worker lifecycle writes the engine runs: the bootstrap, the clean stop, the heartbeat lease
/// refresh, and the dead-worker sweep.
/// </summary>
internal sealed class WorkerLifecycle(
    StartWorkerHandler startWorker,
    StopWorkerHandler stopWorker,
    ExtendWorkerLeasesHandler extendWorkerLeases,
    MarkDeadWorkersHandler markDeadWorkers
)
{
    /// <summary>
    /// Single bootstrap round trip: updates the runtime's <c>namespaces</c> row only when its catalog
    /// hash changed and inserts it only when the name is absent, appends a fresh <c>workers</c> row for
    /// this process, and records a <c>worker.started</c> event, returning both DB-assigned ids. The
    /// worker id is the owner stamp on every claim / lease / execution write.
    /// </summary>
    public Task<StartWorkerResponse> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) =>
        startWorker.HandleAsync(command, ct);

    /// <summary>
    /// Clean-shutdown counterpart to start: flips this process's <c>workers</c> row from
    /// Active/Draining to Stopped and records a <c>worker.stopped</c> event. A no-op when the worker
    /// is already terminal (a hard kill leaves the row Active for the dead sweep to reap).
    /// </summary>
    public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) =>
        stopWorker.HandleAsync(new StopWorkerCommand(namespaceId, workerId), ct);

    /// <summary>
    /// Heartbeat lease refresh for one worker: stamps <c>workers.last_seen_at_utc</c> and pushes forward by one TTL
    /// window the lease of each held execution the worker still leases. Answers those executions, each flagged renewed
    /// or skipped because another transaction held the row; a held execution not answered was cancelled, reclaimed, or
    /// claimed again, and a leased row nothing holds is not renewed, so it lapses. Also answers the namespace's newest
    /// <c>definitions.modified_at_utc</c>. Deliberately does not bump <c>runtimes.version</c>.
    /// </summary>
    public Task<ExtendWorkerLeasesResponse> ExtendWorkerLeasesAsync(
        int workerId,
        int leaseTtlSeconds,
        bool draining,
        IReadOnlyList<(long JobId, int ExecutionNumber)> executions,
        CancellationToken ct
    ) => extendWorkerLeases.HandleAsync(new ExtendWorkerLeasesCommand(workerId, leaseTtlSeconds, draining, executions), ct);

    /// <summary>
    /// Dead-worker sweep across all namespaces: flips Active <c>workers</c> rows whose
    /// <c>last_seen_at_utc</c> is older than the window to Dead, skipping rows another sweep holds, and
    /// emits one <c>worker.died</c> event per reaped worker. The count marked is diagnostic.
    /// </summary>
    public Task<MarkDeadWorkersResponse> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) =>
        markDeadWorkers.HandleAsync(new MarkDeadWorkersCommand(deadAfterSeconds), ct);
}
