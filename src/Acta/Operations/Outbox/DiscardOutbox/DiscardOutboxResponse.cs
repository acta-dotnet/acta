namespace Acta;

/// <summary>What parking a discard of a source's quarantined rows did.</summary>
/// <param name="Action">What the call did; accepted means the command is stored and the next relay pass applies it.</param>
/// <param name="PendingSinceUtc">On a rejection, when the earlier command still waiting to be applied was stored, in UTC; null
/// otherwise.</param>
public sealed record DiscardOutboxResponse(ControlAction Action, DateTime? PendingSinceUtc) : IOutboxControlResponse
{
    int? IControlResponse.Version => null;
}
