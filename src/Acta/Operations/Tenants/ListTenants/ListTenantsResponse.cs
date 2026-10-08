namespace Acta;

/// <summary>One page of <see cref="ITenants.ListAsync"/>.</summary>
public sealed record ListTenantsResponse(
    IReadOnlyList<ListTenantsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListTenantsItem>;
