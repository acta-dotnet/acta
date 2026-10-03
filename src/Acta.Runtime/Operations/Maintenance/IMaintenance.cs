namespace Acta.Runtime;

/// <summary>
/// Retention's one operation, the bounded purge batch, behind the seam the retention coordinator's unit
/// tests fake. <see cref="Maintenance"/> runs it.
/// </summary>
internal interface IMaintenance
{
    /// <summary>
    /// Deletes at most BatchSize eligible roots in the selected section, including their dependents.
    /// A failed batch rolls back; earlier successful batches belong to the sweep coordinator.
    /// </summary>
    Task<int> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct);
}
