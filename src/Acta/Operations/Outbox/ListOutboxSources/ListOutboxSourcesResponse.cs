namespace Acta;

/// <summary>One page of <see cref="IOutbox.ListSourcesAsync"/>.</summary>
public sealed record ListOutboxSourcesResponse(
    IReadOnlyList<ListOutboxSourcesItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListOutboxSourcesItem>;
