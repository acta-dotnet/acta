namespace Acta;

/// <summary>The ledger's housekeeping writes, run by <c>sys.retention</c>.</summary>
internal sealed class MaintenanceOperations(
    PurgeExpiredJobsHandler purgeExpiredJobs,
    PurgeExpiredEventsHandler purgeExpiredEvents,
    PurgeExpiredAlertsHandler purgeExpiredAlerts,
    PurgeExpiredWorkersHandler purgeExpiredWorkers,
    PurgeExpiredLocksHandler purgeExpiredLocks,
    PurgeUnusedLanesHandler purgeUnusedLanes,
    GetAlertFloorHandler getAlertFloor
)
{
    /// <summary>Deletes one batch of the jobs walk.</summary>
    public Task<PurgeExpiredJobsResponse> PurgeExpiredJobsAsync(PurgeExpiredJobsCommand command, CancellationToken ct) =>
        purgeExpiredJobs.HandleAsync(command, ct);

    /// <summary>Deletes one batch of a namespace's expired events.</summary>
    public Task<PurgeExpiredEventsResponse> PurgeExpiredEventsAsync(PurgeExpiredEventsCommand command, CancellationToken ct) =>
        purgeExpiredEvents.HandleAsync(command, ct);

    /// <summary>Deletes one batch of a namespace's expired alerts.</summary>
    public Task<PurgeExpiredAlertsResponse> PurgeExpiredAlertsAsync(PurgeExpiredAlertsCommand command, CancellationToken ct) =>
        purgeExpiredAlerts.HandleAsync(command, ct);

    /// <summary>Deletes one batch of a namespace's ended workers.</summary>
    public Task<PurgeExpiredWorkersResponse> PurgeExpiredWorkersAsync(PurgeExpiredWorkersCommand command, CancellationToken ct) =>
        purgeExpiredWorkers.HandleAsync(command, ct);

    /// <summary>Deletes one batch of expired lock rows.</summary>
    public Task<PurgeExpiredLocksResponse> PurgeExpiredLocksAsync(PurgeExpiredLocksCommand command, CancellationToken ct) =>
        purgeExpiredLocks.HandleAsync(command, ct);

    /// <summary>Deletes one batch of a namespace's unreferenced lanes.</summary>
    public Task<PurgeUnusedLanesResponse> PurgeUnusedLanesAsync(PurgeUnusedLanesCommand command, CancellationToken ct) =>
        purgeUnusedLanes.HandleAsync(command, ct);

    /// <summary>What the namespace's alert projector has consumed, which retention waits behind.</summary>
    public Task<GetAlertFloorResponse> GetAlertFloorAsync(int namespaceId, CancellationToken ct) =>
        getAlertFloor.HandleAsync(new GetAlertFloorQuery(NamespaceId: namespaceId), ct);
}
