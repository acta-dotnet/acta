namespace Acta;

/// <summary>
/// One catalog calendar family as <see cref="ICalendars.GetAsync"/> reads it: its role, <c>CurrentVersion</c> the Published
/// version (null while the family has none), <c>LatestVersion</c> the newest, and <c>ShownVersion</c> with its status and
/// its <c>Definition</c> JSON. <c>Dependents</c> lists every schedule that names the family in its scope, by job name and
/// then schedule name; <see cref="ICalendars.ListVersionsAsync"/> pages the versions.
/// </summary>
public sealed record GetCalendarResponse(
    string JobNamespace,
    string CalendarName,
    CalendarRoleCode Role,
    int? CurrentVersion,
    int LatestVersion,
    int ShownVersion,
    CalendarStatusCode ShownStatus,
    string Definition,
    IReadOnlyList<GetCalendarDependent> Dependents
);
