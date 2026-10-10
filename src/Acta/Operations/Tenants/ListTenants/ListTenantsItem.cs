using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One tenant row in a <see cref="ITenants.ListAsync"/> page, projected from the <c>tenants</c> table.
/// <c>TenantName</c> is the kebab-case name the caller registered, unique, and <c>DisplayName</c> the human label
/// (dashboards fall back to the name); <c>TenantId</c> is the DB-assigned id <c>job.tenant_id</c> references, internal
/// and never serialized, since a tenant is addressed by its key; <c>Status</c> Active resolves at enqueue and Suspended
/// rejects it; <c>Version</c> is the row version a CAS control verb takes as expected.
/// </summary>
public sealed record ListTenantsItem(
    [property: JsonIgnore] int TenantId,
    string TenantName,
    string? DisplayName,
    string? Description,
    TenantStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
