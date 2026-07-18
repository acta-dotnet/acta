namespace Acta;

/// <summary>
/// Tenants domain: register/upsert a tenant in the Acta-owned catalog plus the tenant list. Reached
/// through <see cref="IJobs.Tenants"/>.
/// </summary>
public interface ITenants
{
    /// <summary>
    /// Register (or upsert) a tenant by opaque <paramref name="tenantKey"/> and return its stable numeric
    /// id. Idempotent: a repeat returns the same id, updating display name/description/status only when
    /// changed. <paramref name="displayName"/> is the human display label for dashboards; null keeps the
    /// column empty (dashboards fall back to the key).
    /// </summary>
    ValueTask<int> RegisterAsync(
        string tenantKey,
        string? displayName = null,
        string? description = null,
        TenantStatusCode status = TenantStatusCode.Active,
        CancellationToken ct = default
    );

    /// <summary>List registered tenants ordered by tenant key ascending.</summary>
    ValueTask<PagedResult<TenantListItem>> ListAsync(ListTenantsQuery query, CancellationToken ct = default);

    /// <summary>Suspend a registered tenant by key (status-only, idempotent). Emits tenant.suspended.</summary>
    ValueTask<AdminControlResult> SuspendAsync(
        string tenantKey,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Resume a suspended tenant by key (status-only, idempotent). Emits tenant.resumed.</summary>
    ValueTask<AdminControlResult> ResumeAsync(
        string tenantKey,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Update a tenant display name / description with a version CAS. Null clears the field. Emits tenant.metadata-changed.</summary>
    ValueTask<AdminControlResult> UpdateMetadataAsync(
        string tenantKey,
        string? displayName,
        string? description,
        int expectedVersion,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );
}
