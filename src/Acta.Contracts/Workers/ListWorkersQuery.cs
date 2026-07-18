namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IWorkers.ListAsync"/>. Workers are ordered most
/// recently seen first (last_seen_at_utc descending, id descending).
/// </summary>
/// <param name="JobNamespace">Restrict to one namespace.</param>
/// <param name="Status">Restrict to one worker status.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the filter-wide row count.</param>
/// <param name="Tags">Restrict to workers carrying every supplied exact tag filter.</param>
public sealed record ListWorkersQuery(
    string? JobNamespace = null,
    WorkerStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
