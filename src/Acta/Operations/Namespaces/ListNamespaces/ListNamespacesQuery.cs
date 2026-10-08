namespace Acta;

/// <summary>
/// Filters and paging for <see cref="INamespaces.ListAsync"/>; namespaces are ordered by name ascending.
/// <c>NameContains</c> matches a kebab-case fragment; <c>Status</c> null matches all; <c>PageSize</c> null defaults to 50
/// and clamps at 100; <c>Cursor</c> is the previous page's <see cref="PagedResult{T}.NextCursor"/>; <c>Tags</c> restricts
/// to namespaces carrying every supplied exact filter.
/// </summary>
public sealed record ListNamespacesQuery(
    string? NameContains = null,
    NamespaceStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
