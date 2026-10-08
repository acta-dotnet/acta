namespace Acta;

/// <summary>One page of <see cref="IOutbox.ListSourcesAsync"/>.</summary>
/// <param name="Items">The page's relay sources, at most <c>PageSize</c> of them.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or null on the final page.</param>
/// <param name="HasMore">Whether more relay sources exist beyond this page.</param>
/// <param name="PageSize">The effective page size after defaulting and clamping.</param>
/// <param name="TotalCount">The filter-wide count, populated only when the query set <c>IncludeTotal</c>.</param>
public sealed record ListOutboxSourcesResponse(
    IReadOnlyList<ListOutboxSourcesItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListOutboxSourcesItem>;
