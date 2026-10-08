namespace Acta.Runtime;

/// <summary>
/// One in-flight job of a lease renewal and the execution the row carries, the one it renewed; <see cref="Renewed"/>
/// is false when another transaction held the row, and the execution is then the one the renewal read.
/// </summary>
internal readonly record struct ExtendWorkerLeasesRow(long JobId, int ExecutionNumber, bool Renewed);
