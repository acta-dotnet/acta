using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The single-tenant read model returned by <see cref="ITenants.GetAsync"/>. It carries the same fields as
/// <see cref="ListTenantsItem"/> today and is a distinct type because a detail read can grow additively (usage counts,
/// per-tenant policy) without the list row paying for it. <c>TenantId</c> is the DB-assigned id
/// <c>jobs.tenant_id</c> references, internal and never serialized, since a tenant is addressed by its unique
/// <c>TenantName</c>; <c>Status</c> Active resolves at enqueue and Suspended rejects it; <c>Version</c> is the row
/// version a CAS control verb takes as expected.
/// </summary>
/// <param name="TenantId">The tenant's internal id; the JSON omits it and carries tenantName.</param>
/// <param name="TenantName">The tenant's name.</param>
/// <param name="DisplayName">The tenant's display name.</param>
/// <param name="Description">The tenant's description, when one is recorded.</param>
/// <param name="Status">The tenant's status.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="Version">The tenant's version, which every change to it increments; send it as the expected version of a control.</param>
public sealed record GetTenantResponse(
    [property: JsonIgnore] int TenantId,
    string TenantName,
    string? DisplayName,
    string? Description,
    TenantStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
