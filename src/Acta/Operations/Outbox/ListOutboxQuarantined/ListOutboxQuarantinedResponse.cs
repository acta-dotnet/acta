namespace Acta;

/// <summary>One page of <see cref="IOutbox.ListQuarantinedAsync"/>.</summary>
public sealed record ListOutboxQuarantinedResponse(
    IReadOnlyList<ListOutboxQuarantinedItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListOutboxQuarantinedItem>;
