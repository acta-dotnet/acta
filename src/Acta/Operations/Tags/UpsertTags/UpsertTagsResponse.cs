namespace Acta;

/// <summary>What upserting one tag did: applied, or the target did not exist.</summary>
public sealed record UpsertTagsResponse(TagMutationAction Action) : ITagMutationResponse;
