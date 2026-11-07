namespace Acta;

/// <summary>
/// A calendar version's forecast beside that of the version in force, over the same horizon, so an operator sees what a publication would
/// change: occurrences, windows, and the schedules that name the family, with their overdue flag.
/// </summary>
/// <param name="Role">The calendar family's role, which says how schedules use it.</param>
/// <param name="Version">The number of the version previewed.</param>
/// <param name="CurrentVersion">The number of the published version in force; null when none is published.</param>
/// <param name="OccurrencesUtc">The previewed version's next occurrences, in UTC.</param>
/// <param name="CurrentOccurrencesUtc">The next occurrences of the version in force, in UTC, to compare with.</param>
/// <param name="Windows">The previewed version's next windows.</param>
/// <param name="CurrentWindows">The next windows of the version in force, to compare with.</param>
/// <param name="Schedules">The schedules that name the family, overdue ones first; the list is capped and schedulesTotal counts them
/// all.</param>
/// <param name="SchedulesTotal">How many schedules name the family.</param>
/// <param name="OverdueTotal">How many of those schedules hold an overdue occurrence.</param>
public sealed record PreviewCalendarResponse(
    CalendarRoleCode Role,
    int Version,
    int? CurrentVersion,
    IReadOnlyList<DateTime> OccurrencesUtc,
    IReadOnlyList<DateTime> CurrentOccurrencesUtc,
    IReadOnlyList<CalendarWindow> Windows,
    IReadOnlyList<CalendarWindow> CurrentWindows,
    IReadOnlyList<PreviewCalendarItem> Schedules,
    long SchedulesTotal,
    long OverdueTotal
);

/// <summary>A schedule that names the family, as a publication would meet it: its path, binding, status, and overdue flag.</summary>
/// <param name="JobNamespace">The namespace of the schedule's definition.</param>
/// <param name="JobName">The name of the schedule's definition.</param>
/// <param name="ScheduleName">The schedule's name within its definition.</param>
/// <param name="Binding">The role in which the schedule names the family.</param>
/// <param name="Status">The schedule's status.</param>
/// <param name="Overdue">Whether the schedule holds an occurrence that is overdue under the previewed version.</param>
public sealed record PreviewCalendarItem(
    string JobNamespace,
    string JobName,
    string ScheduleName,
    CalendarRoleCode Binding,
    ScheduleStatusCode Status,
    bool Overdue
);
