namespace Acta;

/// <summary>What updating a tenant did, and the tenant's version after the attempt.</summary>
public sealed record UpdateTenantResponse(ControlAction Action, int? Version) : IControlResponse;
