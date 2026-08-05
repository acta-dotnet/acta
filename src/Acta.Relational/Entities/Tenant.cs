using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// Acta-owned catalog of tenants: the customer / business entity a Job is <em>about</em>. A tenant is an
/// optional, validated scope on individual Jobs, orthogonal to <see cref="JobNamespace"/>, which remains
/// the microservice / work-ownership boundary. Tenants do not own workers, namespaces, job definitions,
/// schedules, or system Jobs; tenant is currently audit / query / runtime scope only, never a scheduling
/// or claim scope. Rows are append-only (no hard delete); <see cref="Status"/> gates whether a key
/// resolves at enqueue. The runtime upserts a row by <see cref="TenantKey"/> via the <c>RegisterTenant</c>
/// handler and reads the assigned id back.
/// </summary>
[DbTable("tenants")]
[DbPrimaryKey(Name = "pk_tenants", Columns = ["id"])]
[DbUniqueIndex(Name = "ux_tenants_key", Columns = ["tenant_key"], Usage = "uniqueness")]
internal sealed class Tenant : IEntity<int>
{
    /// <summary>
    /// DB-assigned identity. Referenced by <c>job.tenant_id</c> and <c>events.tenant_id</c>. Callers
    /// never pick the value; the runtime upserts the row by <see cref="TenantKey"/> and reads the id back.
    /// </summary>
    [DbColumn("id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>
    /// Caller-supplied external tenant identifier (an opaque key such as a GUID, ULID, or customer code,
    /// not a human label). Unique. Validated as opaque (non-empty, no <c>"sys."</c> prefix, at most 128
    /// chars) rather than kebab. Resolved to <see cref="Id"/> at enqueue; the human label lives in
    /// <see cref="DisplayName"/>.
    /// </summary>
    [DbColumn("tenant_key", DbKind.AsciiString, Size = 128)]
    public string TenantKey { get; init; } = default!;

    /// <summary>
    /// Tenant lifecycle status. <see cref="TenantStatusCode.Active"/> resolves at enqueue;
    /// <see cref="TenantStatusCode.Suspended"/> rejects the enqueue (reversible). Supplied at INSERT by
    /// the <c>RegisterTenant</c> handler (no server-side default).
    /// </summary>
    [DbColumn("status_code")]
    public TenantStatusCode Status { get; internal set; }

    /// <summary>Human display label for dashboards and pickers; NULL falls back to the key.</summary>
    [DbColumn("display_name", DbKind.UnicodeString, Size = CatalogLimits.TenantDisplayName)]
    public string? DisplayName { get; internal set; }

    /// <summary>
    /// Longer operator-readable notes about the tenant. Unicode-capable.
    /// </summary>
    [DbColumn("description", DbKind.UnicodeString, Size = CatalogLimits.TenantDescription)]
    public string? Description { get; internal set; }

    /// <summary>When the tenant row was first created. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the tenant row was last updated. Set server-side on every mutation.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token; SPs manually increment on UPDATE.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
