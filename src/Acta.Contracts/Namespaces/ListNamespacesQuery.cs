namespace Acta;

/// <summary>
/// Filters and paging for <see cref="INamespaces.ListAsync"/>. Namespaces are ordered
/// alphabetically (name ascending).
/// </summary>
/// <param name="NameStartsWith">Restrict to namespaces whose name begins with this kebab-case prefix.</param>
/// <param name="Status">Restrict to namespaces in this status; null matches all. Honored by the admin-row list only.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the filter-wide row count.</param>
/// <param name="Tags">Restrict to namespaces carrying every supplied exact tag filter.</param>
public sealed record ListNamespacesQuery(
    string? NameStartsWith = null,
    JobNamespaceStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
