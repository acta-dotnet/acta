namespace Acta;

/// <summary>What updating a tenant did, and the tenant's version after the attempt.</summary>
/// <param name="Action">What the call did: applied; version-conflict, when the expected version is stale; or not-found.</param>
/// <param name="Version">The tenant's version after the call; on a version conflict, the version it has now; null when the tenant was not
/// found.</param>
public sealed record UpdateTenantResponse(ControlAction Action, long? Version) : IControlResponse;
