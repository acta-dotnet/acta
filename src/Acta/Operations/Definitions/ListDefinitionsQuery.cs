namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IDefinitions.ListAsync"/>; definitions are ordered by namespace name, then
/// definition name, then id, ascending. <c>NameContains</c> matches a term in the name independent of namespace;
/// <c>PageSize</c> null defaults to 50 and values above 100 clamp; <c>Cursor</c> is the previous page's
/// <see cref="PagedResult{T}.NextCursor"/>; <c>Tags</c> restricts to definitions carrying every supplied exact tag filter.
/// </summary>
public sealed record ListDefinitionsQuery(
    string? JobNamespace = null,
    string? NameContains = null,
    DefinitionStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
