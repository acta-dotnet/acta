namespace Acta;

/// <summary>One page of <see cref="ILedger.ListEventsAsync"/>.</summary>
/// <param name="Items">The page's events, at most <c>PageSize</c> of them.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or null on the final page.</param>
/// <param name="HasMore">Whether more events exist beyond this page.</param>
/// <param name="PageSize">The effective page size after defaulting and clamping.</param>
/// <param name="TotalCount">The filter-wide count, populated only when the query set <c>IncludeTotal</c>.</param>
public sealed record ListEventsResponse(
    IReadOnlyList<ListEventsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListEventsItem>;
