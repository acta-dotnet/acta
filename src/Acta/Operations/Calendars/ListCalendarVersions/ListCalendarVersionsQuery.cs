namespace Acta;

/// <summary>
/// One page of a calendar family's versions for <see cref="ICalendars.ListVersionsAsync"/>, newest first. <c>PageSize</c>
/// null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous page's <see cref="IPagedResponse{T}.NextCursor"/>;
/// an unknown family answers an empty page.
/// </summary>
public sealed record ListCalendarVersionsQuery(
    CalendarLookup Calendar,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false
);
