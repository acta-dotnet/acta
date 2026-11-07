namespace Acta;

/// <summary>
/// One calendar family: its role, its published and newest version numbers, and the version this answer shows, with its status and
/// definition.
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
