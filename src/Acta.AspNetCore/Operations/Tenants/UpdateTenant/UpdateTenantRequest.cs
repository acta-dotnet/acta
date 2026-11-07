namespace Acta.AspNetCore;

/// <summary>Body of <c>PUT /tenants/{tenantName}</c>. A null field clears it; <c>ExpectedVersion</c> drives the CAS.</summary>
/// <param name="DisplayName">The tenant's display name.</param>
/// <param name="Description">The tenant's description; omitted or null stores none.</param>
/// <param name="ExpectedVersion">The version as the caller last read it; when the row has another version the call answers
/// version-conflict and changes nothing.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record UpdateTenantRequest(
    string? DisplayName = null,
    string? Description = null,
    int? ExpectedVersion = null,
    string? ReasonMessage = null
) : IControlRequest;
