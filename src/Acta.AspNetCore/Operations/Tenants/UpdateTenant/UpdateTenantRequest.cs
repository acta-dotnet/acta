namespace Acta.AspNetCore;

/// <summary>Body of <c>PATCH /tenants/{tenantName}</c>. A null field clears it; <c>ExpectedVersion</c> drives the CAS.</summary>
internal sealed record UpdateTenantRequest(
    string? DisplayName = null,
    string? Description = null,
    int? ExpectedVersion = null,
    string? ReasonMessage = null
) : IControlRequest;
