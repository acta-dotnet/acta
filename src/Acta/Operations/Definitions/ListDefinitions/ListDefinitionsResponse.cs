namespace Acta;

/// <summary>One page of <see cref="IDefinitions.ListAsync"/>.</summary>
public sealed record ListDefinitionsResponse(
    IReadOnlyList<ListDefinitionsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListDefinitionsItem>;
