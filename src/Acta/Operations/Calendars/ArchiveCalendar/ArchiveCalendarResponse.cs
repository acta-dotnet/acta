namespace Acta;

/// <summary>
/// What archiving a calendar family's current version did. The archive is rejected while a schedule still names the family, or when
/// its current version is not the one the caller named.
/// </summary>
/// <param name="Action">What the call did: applied; rejected, when a schedule still names the family or its current version is not the
/// one the caller named; or not-found.</param>
/// <param name="Version">The number of the family's current version: the one archived when applied, the one still in force when
/// rejected; null when the family has no current version.</param>
/// <param name="DependentCount">How many schedules name the family, leaving out orphaned ones and those of a retired definition.</param>
public sealed record ArchiveCalendarResponse(ControlAction Action, long? Version, int DependentCount) : ICalendarControlResponse;
