namespace Acta;

/// <summary>One page of <see cref="INamespaces.ListAsync"/>.</summary>
public sealed record ListNamespacesResponse(
    IReadOnlyList<ListNamespacesItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListNamespacesItem>;
