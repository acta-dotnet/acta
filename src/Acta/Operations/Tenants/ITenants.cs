namespace Acta;

/// <summary>
/// Tenants domain: register a tenant in the Acta-owned catalog plus the tenant list. Reached
/// through <see cref="IActaOperations.Tenants"/>.
/// </summary>
public interface ITenants
{
    /// <summary>
    /// Register a tenant by <paramref name="tenantName"/>, a kebab-case name folded to lowercase, and return its stable numeric id, insert-or-return-existing:
    /// a new tenant is created Active with the supplied fields, while an existing name returns its id and leaves the
    /// row untouched (no status, field, or version change). Status changes go through <see cref="SuspendAsync"/> and
    /// <see cref="ResumeAsync"/>, field edits through <see cref="UpdateAsync"/>. <paramref name="displayName"/> is the
    /// human label for dashboards; null keeps the column empty and dashboards fall back to the name.
    /// </summary>
    ValueTask<RegisterTenantResponse> RegisterAsync(
        string tenantName,
        string? displayName = null,
        string? description = null,
        CancellationToken ct = default
    );

    /// <summary>Point-read one registered tenant by name; null when it does not exist.</summary>
    ValueTask<GetTenantResponse?> GetAsync(string tenantName, CancellationToken ct = default);

    /// <summary>List registered tenants ordered by tenant name ascending.</summary>
    ValueTask<ListTenantsResponse> ListAsync(ListTenantsQuery query, CancellationToken ct = default);

    /// <summary>Suspend a registered tenant by name (status-only, idempotent). Emits tenant.suspended.</summary>
    ValueTask<SuspendTenantResponse> SuspendAsync(
        string tenantName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>Resume a suspended tenant by name (status-only, idempotent). Emits tenant.resumed.</summary>
    ValueTask<ResumeTenantResponse> ResumeAsync(
        string tenantName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>Update a tenant display name / description with a version CAS. Null clears the field. Emits tenant.updated.</summary>
    ValueTask<UpdateTenantResponse> UpdateAsync(
        string tenantName,
        int expectedVersion,
        string? displayName,
        string? description,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );
}
