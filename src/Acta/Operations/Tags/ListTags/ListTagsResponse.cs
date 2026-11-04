namespace Acta;

/// <summary>One page of <see cref="ITags.ListAsync"/>: a target's tags by normalized name, in ordinal order.</summary>
public sealed record ListTagsResponse(IReadOnlyList<ListTagsItem> Items, string? NextCursor, bool HasMore, int PageSize, long? TotalCount)
    : IPagedResponse<ListTagsItem>;
