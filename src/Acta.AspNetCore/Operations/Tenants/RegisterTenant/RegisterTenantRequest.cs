namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /tenants</c>. <see cref="TenantKey"/> is the caller-supplied opaque external identifier.
/// Registration is insert-or-return-existing: a new tenant is created Active, an existing one is returned untouched.
/// </summary>
internal sealed record RegisterTenantRequest(string? TenantKey = null, string? DisplayName = null, string? Description = null);
