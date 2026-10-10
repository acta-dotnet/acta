namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// Target-agnostic <see cref="ITags"/>: every target reads as the set env=prod plus team, and both mutations record
/// their input and report <see cref="MutationAction"/>.
/// </summary>
internal sealed class FakeTags : ITags
{
    public FakeTags()
    {
        Get = static (_, _) =>
            ValueTask.FromResult<GetTagsResponse?>(new GetTagsResponse([new GetTagsItem("env", "prod"), new GetTagsItem("team")]));
        Upsert = (_, tag, _, _, _) =>
        {
            UpsertCalls.Add(tag);
            return ValueTask.FromResult(new UpsertTagsResponse(MutationAction));
        };
        Remove = (_, name, _, _, _) =>
        {
            RemoveCalls.Add(name);
            return ValueTask.FromResult(new RemoveTagsResponse(MutationAction));
        };
    }

    public TagMutationAction MutationAction { get; set; } = TagMutationAction.Applied;

    public List<TagInput> UpsertCalls { get; } = [];

    public List<string> RemoveCalls { get; } = [];

    public Func<TagTarget, CancellationToken, ValueTask<GetTagsResponse?>> Get { get; set; }

    public Func<TagTarget, TagInput, string?, string?, CancellationToken, ValueTask<UpsertTagsResponse>> Upsert { get; set; }

    public Func<TagTarget, string, string?, string?, CancellationToken, ValueTask<RemoveTagsResponse>> Remove { get; set; }

    ValueTask<GetTagsResponse?> ITags.GetAsync(TagTarget target, CancellationToken ct) => Get(target, ct);

    ValueTask<UpsertTagsResponse> ITags.UpsertAsync(
        TagTarget target,
        TagInput tag,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Upsert(target, tag, reasonMessage, actor, ct);

    ValueTask<RemoveTagsResponse> ITags.RemoveAsync(
        TagTarget target,
        string name,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Remove(target, name, reasonMessage, actor, ct);

    ValueTask<ReplaceTagsResponse> ITags.ReplaceAsync(
        TagTarget target,
        IReadOnlyList<TagInput> tags,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => throw new NotSupportedException();
}
