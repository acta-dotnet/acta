namespace Acta;

/// <summary>One page of a source's quarantined outbox rows.</summary>
/// <param name="NextCursor">Opaque cursor for the next page, or null on the final page.</param>
/// <param name="HasMore">Whether more quarantined staging rows exist beyond this page.</param>
/// <param name="PageSize">The effective page size after defaulting and clamping.</param>
/// <param name="Items">The page's items, at most pageSize of them.</param>
/// <param name="TotalCount">The number of rows the filter matches; null unless the request set includeTotal.</param>
public sealed record ListQuarantinedOutboxResponse(
    IReadOnlyList<ListQuarantinedOutboxItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListQuarantinedOutboxItem>;
