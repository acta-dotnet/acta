namespace Acta;

/// <summary>What replacing a target's tags did: applied, or the target did not exist.</summary>
public sealed record ReplaceTagsResponse(TagMutationAction Action) : ITagMutationResponse;
