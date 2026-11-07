namespace Acta;

/// <summary>What resuming a tenant did, and the tenant's version after the attempt.</summary>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Version">The tenant's version after the call; on a version conflict, the version it has now; null when the tenant was not
/// found.</param>
public sealed record ResumeTenantResponse(ControlAction Action, int? Version) : IControlResponse;
