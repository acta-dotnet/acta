namespace Acta;

/// <summary><see cref="ITags"/>, one line per operation.</summary>
internal sealed class TagsOperations(
    ListTagsHandler listTags,
    ReplaceTagsHandler replaceTags,
    UpsertTagHandler upsertTag,
    RemoveTagHandler removeTag
) : ITags
{
    public ValueTask<ListTagsResponse> ListAsync(ListTagsQuery query, CancellationToken ct = default) => listTags.HandleAsync(query, ct);

    public ValueTask<ReplaceTagsResponse> ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct = default) =>
        replaceTags.HandleAsync(new ReplaceTagsCommand(target, tags), ct);

    public ValueTask<UpsertTagResponse> UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct = default) =>
        upsertTag.HandleAsync(new UpsertTagCommand(target, tag), ct);

    public ValueTask<RemoveTagResponse> RemoveAsync(TagTarget target, string name, CancellationToken ct = default) =>
        removeTag.HandleAsync(new RemoveTagCommand(target, name), ct);
}
