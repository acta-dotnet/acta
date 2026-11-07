namespace Acta;

/// <summary>
/// One calendar family in a <see cref="ICalendars.ListAsync"/> page. <c>CurrentVersion</c> and <c>PublishedAtUtc</c> are the
/// Published version's, null while the family has none; <c>LatestVersion</c>, <c>LatestStatus</c>, and <c>ModifiedAtUtc</c>
/// are the highest version's; <c>DependentCount</c> counts the schedules, orphans aside, that bind the family through the
/// catalog: the namespace's, or every namespace's for a shared family in <c>sys</c>.
/// </summary>
/// <param name="JobNamespace">The namespace that holds the calendar family.</param>
/// <param name="CalendarName">The calendar family's name within its namespace.</param>
/// <param name="Role">The calendar family's role, which says how schedules use it.</param>
/// <param name="CurrentVersion">The number of the published version in force; null when none is published.</param>
/// <param name="LatestVersion">The number of the family's newest version.</param>
/// <param name="LatestStatus">The status of the family's newest version.</param>
/// <param name="PublishedAtUtc">When the version in force was published, in UTC; null when none is published.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="DependentCount">How many schedules name the family.</param>
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
