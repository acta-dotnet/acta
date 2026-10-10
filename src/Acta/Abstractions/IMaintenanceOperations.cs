namespace Acta;

/// <summary>The ledger's housekeeping writes, run by <c>sys.retention</c>.</summary>
internal interface IMaintenanceOperations
{
    /// <summary>Deletes one bounded batch of a retention section.</summary>
    Task<PurgeExpiredDataResponse> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct);
}
