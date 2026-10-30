namespace Acta;

/// <summary>
/// A version's forecast beside the current version's over the same horizon, so an operator sees what a publication
/// adds and removes: instants for an occurrence calendar, windows for an availability calendar, and the bound
/// schedules with their overdue flag, which a Refuse publication would reject on.
/// </summary>
public sealed record PreviewCalendarResponse(
    CalendarRoleCode Role,
    int Version,
    int? CurrentVersion,
    IReadOnlyList<DateTime> OccurrencesUtc,
    IReadOnlyList<DateTime> CurrentOccurrencesUtc,
    IReadOnlyList<CalendarWindow> Windows,
    IReadOnlyList<CalendarWindow> CurrentWindows,
    IReadOnlyList<PreviewCalendarItem> Schedules
);

/// <summary>A schedule that names the family, as a publication would meet it: its path, binding, status, and overdue flag.</summary>
public sealed record PreviewCalendarItem(
    string JobNamespace,
    string JobName,
    string ScheduleName,
    CalendarRoleCode Binding,
    ScheduleStatusCode Status,
    bool Overdue
);
