namespace Acta;

/// <summary>
/// One calendar family in a <see cref="ICalendars.ListAsync"/> page. <c>CurrentVersion</c> and <c>PublishedAtUtc</c> are the
/// Published version's, null while the family has none; <c>LatestVersion</c>, <c>LatestStatus</c>, and <c>ModifiedAtUtc</c>
/// are the highest version's; <c>DependentCount</c> counts the schedules, orphans aside, that bind the family through the
/// catalog: the namespace's, or every namespace's for a shared family in <c>sys</c>.
/// </summary>
public sealed record ListCalendarsItem(
    string JobNamespace,
    string CalendarName,
    CalendarRoleCode Role,
    int? CurrentVersion,
    int LatestVersion,
    CalendarStatusCode LatestStatus,
    DateTime? PublishedAtUtc,
    DateTime ModifiedAtUtc,
    int DependentCount
);
