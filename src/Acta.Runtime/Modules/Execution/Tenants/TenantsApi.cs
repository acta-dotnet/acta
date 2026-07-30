using Acta.Querying;

namespace Acta.Modules.Execution.Tenants;

/// <summary>
/// <see cref="ITenants"/> implementation: a thin adapter over the Tenants feature service, which
/// owns key canonicalization, actor stamping, cursor math, and the store delegation.
/// </summary>
internal sealed class TenantsApi(TenantsService tenants) : ITenants
{
    public ValueTask<int> RegisterAsync(
        string tenantKey,
        string? displayName = null,
        string? description = null,
        CancellationToken ct = default
    ) => tenants.RegisterAsync(tenantKey, displayName, description, ct);

    public ValueTask<TenantListItem?> GetAsync(string tenantKey, CancellationToken ct = default) => tenants.GetAsync(tenantKey, ct);

    public ValueTask<PagedResult<TenantListItem>> ListAsync(ListTenantsQuery query, CancellationToken ct = default) =>
        tenants.ListAsync(query, ct);

    public ValueTask<AdminControlResult> SuspendAsync(
        string tenantKey,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => tenants.SuspendAsync(tenantKey, reasonMessage, actorKey, ct);

    public ValueTask<AdminControlResult> ResumeAsync(
        string tenantKey,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => tenants.ResumeAsync(tenantKey, reasonMessage, actorKey, ct);

    public ValueTask<AdminControlResult> UpdateMetadataAsync(
        string tenantKey,
        string? displayName,
        string? description,
        int expectedVersion,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => tenants.UpdateMetadataAsync(tenantKey, displayName, description, expectedVersion, reasonMessage, actorKey, ct);
}
