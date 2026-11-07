namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /tenants/{tenantName}/suspend</c>: the operator's reason, optional like the body itself.</summary>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record SuspendTenantRequest(string? ReasonMessage = null) : IControlRequest;
