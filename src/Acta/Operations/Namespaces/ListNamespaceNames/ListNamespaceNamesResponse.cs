namespace Acta;

/// <summary>One page of <see cref="INamespaces.ListNamesAsync"/>.</summary>
public sealed record ListNamespaceNamesResponse(
    IReadOnlyList<string> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<string>;
