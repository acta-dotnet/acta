namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ITenants.ListAsync"/>; tenants are ordered by <c>tenant_name</c> ascending.
/// <c>NameContains</c> is a case-insensitive substring matched against tenant_name, display_name, or description;
/// <c>Status</c> null matches all; <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous
/// page's <see cref="IPagedResponse{T}.NextCursor"/>; <c>Tags</c> restricts to tenants carrying every supplied exact filter.
/// </summary>
public sealed record ListTenantsQuery(
    string? NameContains = null,
    TenantStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
