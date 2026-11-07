namespace Acta;

/// <summary>
/// What archiving a calendar family's current version did. The archive is rejected while any schedule still names the family.
/// </summary>
/// <param name="Action">What the call did: applied; rejected, while a schedule still names the family; or not-found.</param>
/// <param name="Version">The number of the version that was archived; null when nothing was.</param>
/// <param name="DependentCount">How many schedules name the family.</param>
public sealed record ArchiveCalendarResponse(ControlAction Action, int? Version, int DependentCount) : ICalendarControlResponse;
