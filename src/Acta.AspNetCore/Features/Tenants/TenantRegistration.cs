namespace Acta.AspNetCore.Features.Tenants;

/// <summary>
/// Body of a tenant register POST. <see cref="TenantKey"/> is the caller-supplied opaque external
/// identifier. Registration is insert-or-return-existing: a new tenant is created Active, an
/// existing one is returned untouched. Status changes go through the suspend/resume endpoints.
/// </summary>
internal sealed record TenantRegistrationRequest(string? TenantKey = null, string? DisplayName = null, string? Description = null);

/// <summary>
/// HTTP projection of a registered tenant: the echoed canonical key, which is how every other
/// endpoint addresses a tenant. The DB-assigned id stays internal.
/// </summary>
internal sealed record TenantRegistrationResponse(string TenantKey);
