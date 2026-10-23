namespace Acta;

/// <summary>The ledger's housekeeping writes, run by <c>sys.retention</c>.</summary>
internal sealed class MaintenanceOperations(PurgeExpiredDataHandler purgeExpiredData, GetAlertFloorHandler getAlertFloor)
{
    /// <summary>Deletes one bounded batch of a retention section.</summary>
    public Task<PurgeExpiredDataResponse> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct) =>
        purgeExpiredData.HandleAsync(command, ct);

    /// <summary>What the namespace's alert projector has consumed, which a purge must not run ahead of.</summary>
    public Task<GetAlertFloorResponse> GetAlertFloorAsync(int namespaceId, CancellationToken ct) =>
        getAlertFloor.HandleAsync(new GetAlertFloorQuery(NamespaceId: namespaceId), ct);
}
