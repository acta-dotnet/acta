namespace Acta;

/// <summary>What upserting one tag did: applied, or the target did not exist.</summary>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
public sealed record UpsertTagResponse(ControlAction Action) : ITagMutationResponse
{
    int? IControlResponse.Version => null;
}
