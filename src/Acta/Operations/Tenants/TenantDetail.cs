using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The single-tenant read model returned by <see cref="ITenants.GetAsync"/>. It carries the same fields as
/// <see cref="TenantListItem"/> today and is a distinct type because a detail read can grow additively (usage counts,
/// per-tenant policy) without the list row paying for it. <c>TenantId</c> is the DB-assigned id
/// <c>job.tenant_id</c> references, internal and never serialized, since a tenant is addressed by its unique
/// <c>TenantKey</c>; <c>Status</c> Active resolves at enqueue and Suspended rejects it; <c>Version</c> is the row
/// version a CAS control verb takes as expected.
/// </summary>
public sealed record TenantDetail(
    [property: JsonIgnore] int TenantId,
    string TenantKey,
    string? DisplayName,
    string? Description,
    TenantStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
