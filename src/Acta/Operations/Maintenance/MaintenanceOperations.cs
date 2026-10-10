namespace Acta;

/// <summary><see cref="IMaintenanceOperations"/>, one line per operation.</summary>
internal sealed class MaintenanceOperations(PurgeExpiredDataHandler purgeExpiredData) : IMaintenanceOperations
{
    public Task<PurgeExpiredDataResponse> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct) =>
        purgeExpiredData.HandleAsync(command, ct);
}
