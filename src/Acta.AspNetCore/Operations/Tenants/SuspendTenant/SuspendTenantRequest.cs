namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /tenants/{tenantName}/suspend</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record SuspendTenantRequest(string? ReasonMessage = null) : IControlRequest;
