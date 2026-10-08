namespace Acta;

/// <summary>
/// The tags attached to one existing target, returned by <see cref="ITags.GetAsync"/>: an immutable snapshot ordered by
/// normalized name using ordinal comparison, and empty when the target carries no tags.
/// </summary>
public sealed record GetTagsResponse(IReadOnlyList<GetTagsItem> Items);
