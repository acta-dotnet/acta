namespace Acta;

/// <summary>
/// One held execution this worker still leases; <see cref="Renewed"/> is false when another transaction held the row.
/// </summary>
internal readonly record struct ExtendWorkerLeasesRow(long JobId, int ExecutionNumber, bool Renewed);
