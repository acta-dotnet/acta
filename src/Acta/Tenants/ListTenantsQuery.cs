namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ITenants.ListAsync"/>. Tenants are ordered by
/// <c>tenant_key</c> ascending.
/// </summary>
/// <param name="NameContains">Case-insensitive substring matched against tenant_key, display_name, or description; null matches all.</param>
/// <param name="Status">Restrict to tenants in this status; null matches all.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the total row count.</param>
/// <param name="Tags">Restrict to tenants carrying every supplied exact tag filter.</param>
public sealed record ListTenantsQuery(
    string? NameContains = null,
    TenantStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
