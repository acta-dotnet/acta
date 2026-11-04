namespace Acta;

/// <summary>
/// One page of a target's tags for <see cref="ITags.ListAsync"/>, ordered by normalized name using ordinal comparison.
/// <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous page's
/// <see cref="IPagedResponse{T}.NextCursor"/>; a target that does not exist answers an empty page.
/// </summary>
public sealed record ListTagsQuery(TagTarget Target, int? PageSize = null, string? Cursor = null, bool IncludeTotal = false);
