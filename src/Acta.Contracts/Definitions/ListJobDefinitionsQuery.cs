namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IDefinitions.ListAsync"/>. Definitions are
/// ordered by namespace name, then definition name, then id, all ascending.
/// </summary>
/// <param name="JobNamespace">Restrict to one namespace.</param>
/// <param name="Status">Restrict to one definition status.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the filter-wide row count.</param>
/// <param name="Tags">Restrict to definitions carrying every supplied exact tag filter.</param>
public sealed record ListJobDefinitionsQuery(
    string? JobNamespace = null,
    JobDefinitionStatusCode? Status = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
