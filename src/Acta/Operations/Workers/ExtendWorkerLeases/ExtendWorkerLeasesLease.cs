namespace Acta;

/// <summary>A held execution this worker still leases; <see cref="Renewed"/> is false when another transaction held the row.</summary>
internal readonly record struct ExtendWorkerLeasesLease(long JobId, int ExecutionNumber, bool Renewed);
