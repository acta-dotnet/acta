namespace Acta;

/// <summary>
/// One row of a lease renewal: a held execution this worker still leases, <see cref="Renewed"/> false when another
/// transaction held the row; a lock hold it renewed, by <see cref="LockKey"/>; or the row with neither key, carrying the
/// namespace's newest definition change and whether this call took the namespace's recovery pass.
/// </summary>
internal readonly record struct ExtendWorkerLeasesRow(
    long? JobId,
    int? ExecutionNumber,
    string? LockKey,
    bool Renewed,
    DateTime? DefinitionsModifiedAtUtc,
    bool RecoveryClaimed = false
);
