namespace Acta;

/// <summary>
/// The single-tenant read model returned by <see cref="ITenants.GetAsync"/>. Today it carries the
/// same fields as <see cref="TenantListItem"/>; it is a distinct type because a detail read can grow
/// additively (usage counts, per-tenant policy) without the list row paying for it.
/// </summary>
/// <param name="TenantId">DB-assigned tenant id, referenced by <c>job.tenant_id</c>.</param>
/// <param name="TenantKey">Caller-supplied opaque external identifier. Unique.</param>
/// <param name="DisplayName">Human display label for dashboards, or null (dashboards fall back to the key).</param>
/// <param name="Description">Operator-readable description, or null.</param>
/// <param name="Status">Tenant lifecycle status (Active resolves at enqueue; Suspended rejects it).</param>
/// <param name="CreatedAtUtc">Row insert instant.</param> <param name="ModifiedAtUtc">Last row change instant.</param>
/// <param name="Version">Optimistic-concurrency row version; pass as the expected version to a CAS control verb.</param>
public sealed record TenantDetail(
    int TenantId,
    string TenantKey,
    string? DisplayName,
    string? Description,
    TenantStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
