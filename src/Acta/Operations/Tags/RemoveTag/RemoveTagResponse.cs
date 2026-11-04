namespace Acta;

/// <summary>What removing one tag did: applied, or the target did not exist.</summary>
public sealed record RemoveTagResponse(ControlAction Action) : ITagMutationResponse
{
    int? IControlResponse.Version => null;
}
