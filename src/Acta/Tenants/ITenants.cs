namespace Acta;

/// <summary>
/// Tenants domain: register a tenant in the Acta-owned catalog plus the tenant list. Reached
/// through <see cref="IActaOperations.Tenants"/>.
/// </summary>
public interface ITenants
{
    /// <summary>
    /// Register a tenant by opaque <paramref name="tenantKey"/> and return its stable numeric id.
    /// Insert-or-return-existing: a new tenant is created Active with the supplied metadata; when the
    /// key already exists the existing id is returned and the row is left untouched (no status,
    /// metadata, or version change). Status changes go through <see cref="SuspendAsync"/> /
    /// <see cref="ResumeAsync"/>; metadata through <see cref="UpdateMetadataAsync"/>.
    /// <paramref name="displayName"/> is the human display label for dashboards; null keeps the
    /// column empty (dashboards fall back to the key).
    /// </summary>
    ValueTask<int> RegisterAsync(string tenantKey, string? displayName = null, string? description = null, CancellationToken ct = default);

    /// <summary>Point-read one registered tenant by opaque key; null when it does not exist.</summary>
    ValueTask<TenantListItem?> GetAsync(string tenantKey, CancellationToken ct = default);

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
