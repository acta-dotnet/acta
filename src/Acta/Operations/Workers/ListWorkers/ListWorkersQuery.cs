namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IWorkers.ListAsync"/>; workers are ordered most recently seen first
/// (last_seen_at_utc descending, id descending), by namespace and status. <c>PageSize</c> null defaults to 50 and
/// clamps at 100; <c>Cursor</c> is the previous page's <see cref="PagedResult{T}.NextCursor"/>; <c>Tags</c> restricts
/// to workers carrying every supplied exact tag filter.
/// </summary>
public sealed record ListWorkersQuery(
    string? JobNamespace = null,
    WorkerStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
