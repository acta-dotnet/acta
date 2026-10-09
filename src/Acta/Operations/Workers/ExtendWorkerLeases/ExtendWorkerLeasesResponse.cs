namespace Acta;

/// <summary>
/// Every held execution this worker still leases, each flagged renewed or skipped, the keys of the lock holds it
/// renewed, and the namespace's newest <c>definitions.modified_at_utc</c>, null when it has no definitions.
/// </summary>
internal readonly record struct ExtendWorkerLeasesResponse(
    IReadOnlyList<ExtendWorkerLeasesLease> Leases,
    IReadOnlyList<string> RenewedLockKeys,
    DateTime? DefinitionsModifiedAtUtc
);
