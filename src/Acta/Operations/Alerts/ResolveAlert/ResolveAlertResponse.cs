namespace Acta;

/// <summary>What resolving an alert did, and its timestamps after the attempt.</summary>
/// <param name="AlertRef">The alert's ref, alr_ followed by 32 hex digits.</param>
/// <param name="Action">What the call did: applied, or not-found.</param>
/// <param name="AcknowledgedAtUtc">When the alert was acknowledged, in UTC; null while it has not been.</param>
/// <param name="ResolvedAtUtc">When the alert was resolved, in UTC; null while it is open.</param>
public sealed record ResolveAlertResponse(AlertRef AlertRef, ControlAction Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc)
    : IAlertControlResponse
{
    int? IControlResponse.Version => null;
}
