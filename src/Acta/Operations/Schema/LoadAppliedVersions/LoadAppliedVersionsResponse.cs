namespace Acta;

/// <summary>
/// Every version the history records, with the name it was recorded under.
/// </summary>
internal readonly record struct LoadAppliedVersionsResponse(IReadOnlyDictionary<int, string> Versions);
