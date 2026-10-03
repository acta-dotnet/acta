namespace Acta;

/// <summary>What suspending a tenant did, and the tenant's version after the attempt.</summary>
public sealed record SuspendTenantResponse(ControlAction Action, int? Version) : IControlResponse;
