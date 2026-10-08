namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ISchedules.ListAsync"/>; schedules are ordered next run first (next_run_at_utc
/// ascending, id ascending) and rows without a next run are excluded. <c>JobNamespace</c> is required with
/// <c>JobName</c>; <c>LiveOnly</c> null or true (the default) returns only schedules with no orphaned instant, false
/// includes orphaned ones; <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous page's
/// <see cref="PagedResult{T}.NextCursor"/>; <c>Tags</c> restricts to schedules carrying every supplied exact filter.
/// </summary>
public sealed record ListSchedulesQuery(
    string? JobNamespace = null,
    string? JobName = null,
    ScheduleOriginCode? Origin = null,
    bool? LiveOnly = true,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
