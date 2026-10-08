namespace Acta;

/// <summary>One page of <see cref="IWorkers.ListAsync"/>.</summary>
public sealed record ListWorkersResponse(
    IReadOnlyList<ListWorkersItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListWorkersItem>;
