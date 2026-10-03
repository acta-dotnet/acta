namespace Acta.Runtime;

/// <summary>
/// The worker lifecycle writes the engine runs: the bootstrap, the clean stop, the heartbeat lease
/// refresh, and the dead-worker sweep. <see cref="WorkerLifecycle"/> runs each; the interface is the
/// seam engine unit tests fake.
/// </summary>
internal interface IWorkerLifecycle
{
    /// <summary>
    /// Single bootstrap round trip: updates the runtime's <c>namespaces</c> row only when its catalog
    /// hash changed and inserts it only when the name is absent, appends a fresh <c>workers</c> row for
    /// this process, and records a <c>worker.started</c> event, returning both DB-assigned ids. The
    /// worker id is the owner stamp on every claim / lease / execution write.
    /// </summary>
    Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct);

    /// <summary>
    /// Clean-shutdown counterpart to start: flips this process's <c>workers</c> row from
    /// Active/Draining to Stopped and records a <c>worker.stopped</c> event. A no-op when the worker
    /// is already terminal (a hard kill leaves the row Active for the dead sweep to reap).
    /// </summary>
    Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct);

    /// <summary>
    /// Heartbeat lease refresh for one worker: stamps <c>workers.last_seen_at_utc</c> and pushes every
    /// in-flight job lease this worker holds forward by one TTL window. Returns every in-flight job id it
    /// read, each flagged renewed or skipped because another transaction held the row; the heartbeat diffs
    /// the ids against the jobs it is running to detect ones cancelled or stolen externally. Deliberately
    /// does not bump <c>runtimes.version</c>.
    /// </summary>
    Task<IReadOnlyList<LeaseRenewalRow>> ExtendWorkerLeasesAsync(int workerId, int leaseTtlSeconds, bool draining, CancellationToken ct);

    /// <summary>
    /// Dead-worker sweep across all namespaces: flips Active <c>workers</c> rows whose
    /// <c>last_seen_at_utc</c> is older than the window to Dead, skipping rows another sweep holds, and
    /// emits one <c>worker.died</c> event per reaped worker. Returns the count marked (diagnostic).
    /// </summary>
    Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct);
}
