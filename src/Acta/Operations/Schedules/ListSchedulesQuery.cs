namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ISchedules.ListAsync"/>. Schedules are ordered
/// next run first (next_run_at_utc ascending, id ascending); rows without a next run are excluded.
/// </summary>
/// <param name="JobNamespace">Restrict to one namespace; required when <paramref name="JobName"/> is set.</param>
/// <param name="JobName">Restrict to one job definition name within <paramref name="JobNamespace"/>.</param>
/// <param name="Origin">Restrict to one schedule origin.</param>
/// <param name="LiveOnly">Null or true (the default) returns only schedules with no orphaned instant; false includes orphaned ones too.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the filter-wide row count.</param>
/// <param name="Tags">Restrict to schedules carrying every supplied exact tag filter.</param>
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
