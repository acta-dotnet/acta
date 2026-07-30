namespace Acta.Features.Tags;

internal interface ITagStore
{
    Task<TagSet?> GetAsync(ResolvedTagTarget target, CancellationToken ct);

    Task<TagMutationResult> ApplyAsync(ResolvedTagTarget target, TagMutation mutation, CancellationToken ct);
}

internal sealed record ResolvedTagTarget(TagScopeCode ScopeCode, long? LookupId, string? LookupName);

internal sealed record TagMutation(TagMutationKind Kind, string ItemsJson);

internal enum TagMutationKind : byte
{
    Replace = 1,
    Upsert = 2,
    Remove = 3,
}
