namespace Acta;

/// <summary>
/// What archiving a family's current version did. <c>Version</c> is the family's current version, archived when Applied;
/// <c>DependentCount</c> counts the live schedules of active definitions that name the family in its scope, which reject
/// the archive while any remain.
/// </summary>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Version">The number of the version that was archived; null when nothing was.</param>
/// <param name="DependentCount">How many schedules name the family.</param>
public sealed record ArchiveCalendarResponse(ControlAction Action, int? Version, int DependentCount) : ICalendarControlResponse;
