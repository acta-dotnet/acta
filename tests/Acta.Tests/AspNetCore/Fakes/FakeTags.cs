namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// Target-agnostic <see cref="ITags"/>: every target lists as one page holding env=prod plus team, each list query is
/// recorded, and both mutations record their input and report <see cref="MutationAction"/>.
/// </summary>
internal sealed class FakeTags : ITags
{
    public FakeTags()
    {
        List = (query, _) =>
        {
            ListCalls.Add(query);
            return ValueTask.FromResult(
                new ListTagsResponse([new ListTagsItem("env", "prod"), new ListTagsItem("team")], null, false, query.PageSize ?? 50, null)
            );
        };
        Upsert = (_, tag, _) =>
        {
            UpsertCalls.Add(tag);
            return ValueTask.FromResult(new UpsertTagResponse(MutationAction));
        };
        Remove = (_, name, _) =>
        {
            RemoveCalls.Add(name);
            return ValueTask.FromResult(new RemoveTagResponse(MutationAction));
        };
    }

    public ControlAction MutationAction { get; set; } = ControlAction.Applied;

    public List<TagInput> UpsertCalls { get; } = [];

    public List<string> RemoveCalls { get; } = [];

    public List<ListTagsQuery> ListCalls { get; } = [];

    public Func<ListTagsQuery, CancellationToken, ValueTask<ListTagsResponse>> List { get; set; }

    public Func<TagTarget, TagInput, CancellationToken, ValueTask<UpsertTagResponse>> Upsert { get; set; }

    public Func<TagTarget, string, CancellationToken, ValueTask<RemoveTagResponse>> Remove { get; set; }

    ValueTask<ListTagsResponse> ITags.ListAsync(ListTagsQuery query, CancellationToken ct) => List(query, ct);

    ValueTask<UpsertTagResponse> ITags.UpsertAsync(TagTarget target, TagInput tag, CancellationToken ct) => Upsert(target, tag, ct);

    ValueTask<RemoveTagResponse> ITags.RemoveAsync(TagTarget target, string name, CancellationToken ct) => Remove(target, name, ct);

    ValueTask<ReplaceTagsResponse> ITags.ReplaceAsync(TagTarget target, IReadOnlyList<TagInput> tags, CancellationToken ct) =>
        throw new NotSupportedException();
}
