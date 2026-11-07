namespace Acta;

/// <summary>What parking a requeue of a source's quarantined rows did.</summary>
/// <param name="Action">What the call did; accepted means the command is stored and the next relay pass applies it.</param>
/// <param name="PendingSinceUtc">When the command still waiting to be applied was stored, in UTC; null when none is waiting.</param>
public sealed record RequeueOutboxResponse(ControlAction Action, DateTime? PendingSinceUtc) : IOutboxControlResponse
{
    int? IControlResponse.Version => null;
}
