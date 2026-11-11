namespace Acta;

/// <summary>What resuming a tenant did, and the tenant's version after the attempt.</summary>
/// <param name="Action">What the call did: applied; already-in-state, when it was already as asked; or not-found.</param>
/// <param name="Version">The tenant's version after the call; null when the tenant was not found.</param>
public sealed record ResumeTenantResponse(ControlAction Action, long? Version) : IControlResponse;
