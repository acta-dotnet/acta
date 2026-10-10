namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /tenants/{tenantName}/resume</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record ResumeTenantRequest(string? ReasonMessage = null) : IControlRequest;
