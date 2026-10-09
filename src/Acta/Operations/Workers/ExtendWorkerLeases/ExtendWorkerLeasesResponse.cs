namespace Acta;

/// <summary>Every held execution this worker still leases, each flagged renewed or skipped.</summary>
internal readonly record struct ExtendWorkerLeasesResponse(IReadOnlyList<ExtendWorkerLeasesRow> Leases);
