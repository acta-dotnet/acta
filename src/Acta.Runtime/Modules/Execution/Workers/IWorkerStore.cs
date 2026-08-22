namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Persistence port for worker lifecycle: the atomic bootstrap (namespace upsert + worker row +
/// <c>worker.started</c> event), the clean-shutdown stop, the heartbeat lease refresh, the global
/// dead-worker sweep, and the paged operator list.
/// </summary>
internal interface IWorkerStore
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
    /// in-flight job lease this worker holds forward by one TTL window. Returns the extended job ids;
    /// the heartbeat diffs that against the jobs it is running to detect ones cancelled or stolen
    /// externally. Deliberately does not bump <c>runtimes.version</c>.
    /// </summary>
    Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(int workerId, int leaseTtlSeconds, bool draining, CancellationToken ct);

    /// <summary>
    /// Dead-worker sweep across all namespaces: flips Active <c>workers</c> rows whose
    /// <c>last_seen_at_utc</c> is older than the window to Dead, skipping rows another sweep holds, and
    /// emits one <c>worker.died</c> event per reaped worker. Returns the count marked (diagnostic).
    /// </summary>
    Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct);

    /// <summary>
    /// Read one fully projected worker by its public ref, or <see langword="null"/> when absent.
    /// </summary>
    ValueTask<WorkerDetail?> GetWorkerAsync(Guid workerRef, CancellationToken ct);

    /// <summary>
    /// One keyset page of <c>workers</c> rows ordered <c>last_seen_at_utc DESC, id DESC</c> plus the
    /// opt-in filter-wide total, fetched in one round trip as two result sets.
    /// </summary>
    Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct);
}

/// <summary>
/// Validated worker bootstrap; construct via <see cref="Create"/> so the namespace canonicalization
/// and catalog-hash gate value are computed once, identically for every caller.
/// </summary>
internal sealed record StartWorkerCommand(
    string NamespaceName,
    string? OwnerTeam,
    string? Description,
    string CatalogHash,
    string HostName,
    string DeploymentVersion,
    string? EngineVersion,
    string? DotnetVersion,
    int ProcessId,
    int MaxConcurrency,
    Guid WorkerRef
)
{
    public static StartWorkerCommand Create(
        string namespaceName,
        string? ownerTeam,
        string? description,
        string hostName,
        string deploymentVersion,
        string? engineVersion,
        string? dotnetVersion,
        int processId,
        int maxConcurrency,
        Guid workerRef
    ) =>
        new(
            IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName)),
            ownerTeam,
            description,
            Definitions.CatalogHash.Of(ownerTeam, description),
            hostName,
            deploymentVersion,
            engineVersion,
            dotnetVersion,
            processId,
            maxConcurrency,
            workerRef
        );
}

/// <summary>Decoded workers list request; <c>Take</c> carries the page-size-plus-one peek-ahead.</summary>
internal sealed record WorkerPageRequest(
    string? JobNamespace,
    WorkerStatusCode? Status,
    DateTime? CursorLastSeenAtUtc,
    int? CursorId,
    int Take,
    bool IncludeTotal,
    string? TagFiltersJson = null
);

/// <summary>One page of mapped worker list items plus the opt-in filtered total.</summary>
internal sealed record WorkerPage(IReadOnlyList<WorkerListItem> Rows, long? Total);
