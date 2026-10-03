namespace Acta.Runtime;

/// <summary><see cref="IMaintenance"/>, one line per operation.</summary>
internal sealed class Maintenance(PurgeExpiredDataHandler purgeExpiredData) : IMaintenance
{
    public Task<int> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct) =>
        purgeExpiredData.HandleAsync(command, ct);
}
