namespace Acta;

/// <summary>
/// One tenant row in a <see cref="ITenants.ListAsync"/> page, projected from
/// the <c>tenants</c> table. The <see cref="TenantKey"/> is the caller-supplied opaque external
/// identifier; the human label lives in <see cref="Description"/>.
/// </summary>
/// <param name="TenantId">DB-assigned tenant id, referenced by <c>job.tenant_id</c>.</param>
/// <param name="TenantKey">Caller-supplied opaque external identifier. Unique.</param>
/// <param name="DisplayName">Human display label for dashboards, or null (dashboards fall back to the key).</param>
/// <param name="Description">Operator-readable description, or null.</param>
/// <param name="Status">Tenant lifecycle status (Active resolves at enqueue; Suspended rejects it).</param>
/// <param name="CreatedAtUtc">Row insert instant.</param> <param name="ModifiedAtUtc">Last row change instant.</param>
/// <param name="Version">Optimistic-concurrency row version; pass as the expected version to a CAS control verb.</param>
public sealed record TenantListItem(
    int TenantId,
    string TenantKey,
    string? DisplayName,
    string? Description,
    TenantStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
