namespace Acta;

/// <summary>Every in-flight job the renewal read, each flagged renewed or skipped.</summary>
internal readonly record struct ExtendWorkerLeasesResponse(IReadOnlyList<ExtendWorkerLeasesRow> Leases);
