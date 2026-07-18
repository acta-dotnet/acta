namespace Acta.AspNetCore.Features.Tenants;

/// <summary>
/// Body of a tenant register/suspend POST. <see cref="TenantKey"/> is the caller-supplied opaque
/// external identifier; the upsert is idempotent. A null <see cref="Status"/> defaults to Active;
/// suspending a tenant is the same call with <see cref="TenantStatusCode.Suspended"/>.
/// </summary>
internal sealed record TenantRegistrationRequest(
    string? TenantKey = null,
    string? DisplayName = null,
    string? Description = null,
    TenantStatusCode? Status = null
);

/// <summary>
/// HTTP projection of a registered tenant: the DB-assigned id, the echoed key, and the effective status.
/// </summary>
internal sealed record TenantRegistrationResponse(int TenantId, string TenantKey, TenantStatusCode Status);
