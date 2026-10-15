namespace Acta;

/// <summary>Every migration the history records, by the file stem it was recorded under.</summary>
internal readonly record struct LoadAppliedMigrationsResponse(IReadOnlySet<string> Names);
