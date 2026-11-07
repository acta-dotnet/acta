namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /tenants</c>. <see cref="TenantName"/> is a kebab-case name, folded to lowercase.
/// Registration is insert-or-return-existing: a new tenant is created Active, an existing one is returned untouched.
/// </summary>
/// <param name="TenantName">The tenant's name.</param>
/// <param name="DisplayName">The tenant's display name.</param>
/// <param name="Description">The tenant's description; omitted or null stores none.</param>
internal sealed record RegisterTenantRequest(string? TenantName = null, string? DisplayName = null, string? Description = null);
