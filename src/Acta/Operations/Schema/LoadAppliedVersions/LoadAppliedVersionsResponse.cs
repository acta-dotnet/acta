namespace Acta;

/// <summary>
/// Every version the history records, with the name it was recorded under: the migrations and the version-0 baseline
/// stamp.
/// </summary>
internal readonly record struct LoadAppliedVersionsResponse(IReadOnlyDictionary<int, string> Versions);
