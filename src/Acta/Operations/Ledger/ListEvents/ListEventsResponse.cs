namespace Acta;

/// <summary>One page of <see cref="ILedger.ListEventsAsync"/>.</summary>
public sealed record ListEventsResponse(
    IReadOnlyList<ListEventsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListEventsItem>;
