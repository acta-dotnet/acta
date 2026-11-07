namespace Acta;

/// <summary>
/// One catalog calendar family as <see cref="ICalendars.GetAsync"/> reads it: its role, <c>CurrentVersion</c> the Published
/// version (null while the family has none), <c>LatestVersion</c> the newest, and <c>ShownVersion</c> with its status and
/// its <c>Definition</c> JSON. <see cref="ICalendars.ListVersionsAsync"/> pages the versions and
/// <see cref="ICalendars.ListDependentsAsync"/> the schedules naming the family.
/// </summary>
/// <param name="JobNamespace">The namespace that holds the calendar family.</param>
/// <param name="CalendarName">The calendar family's name within its namespace.</param>
/// <param name="Role">The calendar family's role, which says how schedules use it.</param>
/// <param name="CurrentVersion">The number of the published version in force; null when none is published.</param>
/// <param name="LatestVersion">The number of the family's newest version.</param>
/// <param name="ShownVersion">The number of the version this answer shows.</param>
/// <param name="ShownStatus">The status of the version this answer shows.</param>
/// <param name="Definition">The shown version's definition, as JSON text.</param>
public sealed record GetCalendarResponse(
    string JobNamespace,
    string CalendarName,
    CalendarRoleCode Role,
    int? CurrentVersion,
    int LatestVersion,
    int ShownVersion,
    CalendarStatusCode ShownStatus,
    string Definition
);
