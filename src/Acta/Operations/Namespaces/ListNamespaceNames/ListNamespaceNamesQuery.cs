namespace Acta;

/// <summary>
/// Filters and paging for <see cref="INamespaces.ListNamesAsync"/>; names are ordered ascending.
/// <c>NameContains</c> matches a kebab-case fragment; <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c>
/// is the previous page's <see cref="PagedResult{T}.NextCursor"/>; <c>Tags</c> restricts to namespaces carrying every
/// supplied exact filter.
/// </summary>
public sealed record ListNamespaceNamesQuery(
    string? NameContains = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
