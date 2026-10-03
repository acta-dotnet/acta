namespace Acta;

/// <summary>What resuming a tenant did, and the tenant's version after the attempt.</summary>
public sealed record ResumeTenantResponse(ControlAction Action, int? Version) : IControlResponse;
