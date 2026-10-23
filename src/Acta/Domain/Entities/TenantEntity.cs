namespace Acta;

/// <summary>
/// The tenants a job can be about: an optional, validated scope on individual jobs, orthogonal to the namespace,
/// which stays the work-ownership boundary. A tenant scopes audit, queries, and admission, never scheduling or claims.
/// Rows are never deleted; <see cref="Status"/> gates whether a key resolves at enqueue.
/// </summary>
[DbTable("tenants")]
[DbPrimaryKey(Name = "pk_tenants", Columns = ["tenant_id"])]
[DbUniqueIndex(Name = "ux_tenants_name", Columns = ["tenant_name"], Usage = "uniqueness")]
internal sealed class TenantEntity : IEntity<int>
{
    /// <summary>
    /// DB-assigned identity. Referenced by <c>jobs.tenant_id</c> and <c>events.tenant_id</c>. Callers
    /// never pick the value; the runtime upserts the row by <see cref="TenantName"/> and reads the id back.
    /// </summary>
    [DbColumn("tenant_id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>
    /// Caller-supplied tenant name (a customer code folded to dotted kebab, not a human label). Unique.
    /// Trimmed and lowercased, at most 128 chars, never <c>sys</c> or under <c>sys.</c>. Resolved to
    /// <see cref="Id"/> at enqueue; the human label lives in <see cref="DisplayName"/>.
    /// </summary>
    [DbColumn("tenant_name", DbKind.AsciiString, Size = 128)]
    public string TenantName { get; init; } = default!;

    /// <summary>
    /// Tenant lifecycle status. <see cref="TenantStatusCode.Active"/> resolves at enqueue;
    /// <see cref="TenantStatusCode.Suspended"/> rejects the enqueue (reversible). Supplied at INSERT by
    /// the <c>RegisterTenant</c> handler (no server-side default).
    /// </summary>
    [DbColumn("tenant_status_code")]
    public TenantStatusCode Status { get; internal set; }

    /// <summary>Human display label for dashboards and pickers; NULL falls back to the key.</summary>
    [DbColumn("display_name", DbKind.UnicodeString, Size = TextLimits.TenantDisplayName)]
    public string? DisplayName { get; internal set; }

    /// <summary>
    /// Longer operator-readable notes about the tenant. Unicode-capable.
    /// </summary>
    [DbColumn("description", DbKind.UnicodeString, Size = TextLimits.TenantDescription)]
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
