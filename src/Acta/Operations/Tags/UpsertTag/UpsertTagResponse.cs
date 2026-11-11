namespace Acta;

/// <summary>What upserting one tag did: applied, or the target did not exist.</summary>
/// <param name="Action">What the call did: applied, or not-found.</param>
public sealed record UpsertTagResponse(ControlAction Action) : ITagMutationResponse
{
    long? IControlResponse.Version => null;
}
