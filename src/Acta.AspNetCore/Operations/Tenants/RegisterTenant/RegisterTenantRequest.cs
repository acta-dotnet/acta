namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /tenants</c>. <see cref="TenantName"/> is a kebab-case name, folded to lowercase.
/// Registration is insert-or-return-existing: a new tenant is created Active, an existing one is returned untouched.
/// </summary>
internal sealed record RegisterTenantRequest(string? TenantName = null, string? DisplayName = null, string? Description = null);
