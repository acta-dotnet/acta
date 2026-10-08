namespace Acta;

/// <summary>
/// Every version the history records, with the name it was recorded under: the migrations, the version-0 baseline stamp,
/// and the object package row.
/// </summary>
internal readonly record struct LoadAppliedVersionsResponse(IReadOnlyDictionary<int, string> Versions);
