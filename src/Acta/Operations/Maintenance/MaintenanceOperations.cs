namespace Acta;

/// <summary>The ledger's housekeeping writes, run by <c>sys.retention</c>.</summary>
internal sealed class MaintenanceOperations(PurgeExpiredDataHandler purgeExpiredData)
{
    /// <summary>Deletes one bounded batch of a retention section.</summary>
    public Task<PurgeExpiredDataResponse> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct) =>
        purgeExpiredData.HandleAsync(command, ct);
}
