namespace Acta;

/// <summary>
/// One row of a lease renewal: a held execution this worker still leases, <see cref="Renewed"/> false when another
/// transaction held the row, or the row with a null <see cref="JobId"/> carrying the namespace's newest definition change.
/// </summary>
internal readonly record struct ExtendWorkerLeasesRow(long? JobId, int? ExecutionNumber, bool Renewed, DateTime? DefinitionsModifiedAtUtc);
