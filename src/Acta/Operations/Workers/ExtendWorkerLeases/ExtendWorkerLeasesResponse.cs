namespace Acta;

/// <summary>
/// Every held execution this worker still leases, each flagged renewed or skipped, the keys of the lock holds it
/// renewed, the namespace's newest <c>definitions.modified_at_utc</c> (null when it has no definitions), and whether
/// this call took the namespace's recovery pass, which the worker then runs.
/// </summary>
internal readonly record struct ExtendWorkerLeasesResponse(
    IReadOnlyList<ExtendWorkerLeasesLease> Leases,
    IReadOnlyList<string> RenewedLockKeys,
    DateTime? DefinitionsModifiedAtUtc,
    bool RecoveryClaimed = false
);
