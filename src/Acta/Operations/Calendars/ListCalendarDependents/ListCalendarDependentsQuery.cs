namespace Acta;

/// <summary>
/// One page of the schedules naming a calendar family for <see cref="ICalendars.ListDependentsAsync"/>, by namespace, job,
/// schedule, then binding. <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous page's
/// <see cref="IPagedResponse{T}.NextCursor"/>; an unknown family answers an empty page.
/// </summary>
public sealed record ListCalendarDependentsQuery(
    CalendarLookup Calendar,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false
);
