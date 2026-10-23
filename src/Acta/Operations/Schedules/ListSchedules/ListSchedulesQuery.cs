namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ISchedules.ListAsync"/>. <c>LiveOnly</c> null or true (the default) returns the live
/// schedules with an upcoming occurrence, next occurrence first, then by slot job and schedule name; false returns every
/// schedule, orphaned, paused, and exhausted ones included, by slot job and schedule name. <c>JobNamespace</c> is
/// required with <c>JobName</c>; <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous
/// page's <see cref="IPagedResponse{T}.NextCursor"/>.
/// </summary>
public sealed record ListSchedulesQuery(
    string? JobNamespace = null,
    string? JobName = null,
    ScheduleOriginCode? Origin = null,
    bool? LiveOnly = true,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false
);
