namespace Acta;

/// <summary><see cref="ITags"/>, one line per operation.</summary>
internal sealed class TagsOperations(
    GetTagsHandler getTags,
    ReplaceTagsHandler replaceTags,
    UpsertTagsHandler upsertTags,
    RemoveTagsHandler removeTags
) : ITags
{
    public ValueTask<GetTagsResponse?> GetAsync(TagTarget target, CancellationToken ct = default) =>
        getTags.HandleAsync(new GetTagsQuery(target), ct);

    public ValueTask<ReplaceTagsResponse> ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct = default) =>
        replaceTags.HandleAsync(new ReplaceTagsCommand(target, tags), ct);

    public ValueTask<UpsertTagsResponse> UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct = default) =>
        upsertTags.HandleAsync(new UpsertTagsCommand(target, tag), ct);

    public ValueTask<RemoveTagsResponse> RemoveAsync(TagTarget target, string name, CancellationToken ct = default) =>
        removeTags.HandleAsync(new RemoveTagsCommand(target, name), ct);
}
