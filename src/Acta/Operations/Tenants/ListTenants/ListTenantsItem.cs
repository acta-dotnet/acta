using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One tenant row in a <see cref="ITenants.ListAsync"/> page, projected from the <c>tenants</c> table.
/// <c>TenantName</c> is the kebab-case name the caller registered, unique, and <c>DisplayName</c> the human label
/// (dashboards fall back to the name); <c>TenantId</c> is the DB-assigned id <c>jobs.tenant_id</c> references, internal
/// and never serialized, since a tenant is addressed by its key; <c>Status</c> Active resolves at enqueue and Suspended
/// rejects it; <c>Version</c> is the row version a CAS control verb takes as expected.
/// </summary>
/// <param name="TenantId">The tenant's internal id; the JSON omits it and carries tenantName.</param>
/// <param name="TenantName">The tenant's name.</param>
/// <param name="DisplayName">The tenant's display name.</param>
/// <param name="Description">The tenant's description, when one is recorded.</param>
/// <param name="Status">The tenant's status.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="Version">The tenant's version, which every change to it increments; send it as the expected version of a control.</param>
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
