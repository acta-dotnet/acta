namespace Acta;

/// <summary><see cref="ITags"/>, one line per operation.</summary>
internal sealed class TagsOperations(
    ListTagsHandler listTags,
    ReplaceTagsHandler replaceTags,
    UpsertTagHandler upsertTag,
    RemoveTagHandler removeTag
) : ITags
{
    public Task<ListTagsResponse> ListAsync(ListTagsQuery query, CancellationToken ct = default) => listTags.HandleAsync(query, ct);

    public Task<ReplaceTagsResponse> ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct = default) =>
        replaceTags.HandleAsync(new ReplaceTagsCommand(target, tags), ct);

    public Task<UpsertTagResponse> UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct = default) =>
        upsertTag.HandleAsync(new UpsertTagCommand(target, tag), ct);

    public Task<RemoveTagResponse> RemoveAsync(TagTarget target, string name, CancellationToken ct = default) =>
        removeTag.HandleAsync(new RemoveTagCommand(target, name), ct);
}
