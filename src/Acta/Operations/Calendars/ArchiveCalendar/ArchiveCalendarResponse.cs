namespace Acta;

/// <summary>
/// What archiving a family's current version did. <c>Version</c> is the family's current version, archived when Applied;
/// <c>DependentCount</c> counts the live schedules of active definitions that bind the family through the catalog, which
/// reject the archive while any remain.
/// </summary>
public sealed record ArchiveCalendarResponse(ControlAction Action, int? Version, int DependentCount) : ICalendarControlResponse;
