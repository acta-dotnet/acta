namespace Acta;

/// <summary>
/// One catalog calendar family as <see cref="ICalendars.GetAsync"/> reads it: its role, <c>CurrentVersion</c> the Published
/// version (null while the family has none), <c>LatestVersion</c> the newest, and <c>ShownVersion</c> with its status and
/// its <c>Definition</c> JSON. <see cref="ICalendars.ListVersionsAsync"/> pages the versions and
/// <see cref="ICalendars.ListDependentsAsync"/> the schedules naming the family.
/// </summary>
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
