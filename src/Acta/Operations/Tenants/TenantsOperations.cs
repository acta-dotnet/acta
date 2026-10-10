namespace Acta;

/// <summary>The Tenants operations: <see cref="ITenants"/> for applications, plus the engine's calls.</summary>
internal sealed class TenantsOperations(
    RegisterTenantHandler registerTenant,
    GetTenantHandler getTenant,
    ListTenantsHandler listTenants,
    SuspendTenantHandler suspendTenant,
    ResumeTenantHandler resumeTenant,
    UpdateTenantHandler updateTenant,
    GetTenantKeyHandler getTenantKey
) : ITenants
{
    public ValueTask<RegisterTenantResponse> RegisterAsync(
        string tenantKey,
        string? displayName = null,
        string? description = null,
        CancellationToken ct = default
    ) => registerTenant.HandleAsync(new RegisterTenantCommand(tenantKey, displayName, description), ct);

    public ValueTask<GetTenantResponse?> GetAsync(string tenantKey, CancellationToken ct = default) =>
        getTenant.HandleAsync(new GetTenantQuery(tenantKey), ct);

    public ValueTask<ListTenantsResponse> ListAsync(ListTenantsQuery query, CancellationToken ct = default) =>
        listTenants.HandleAsync(query, ct);

    public ValueTask<SuspendTenantResponse> SuspendAsync(
        string tenantKey,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => suspendTenant.HandleAsync(new SuspendTenantCommand(tenantKey, reasonMessage, actorKey), ct);

    public ValueTask<ResumeTenantResponse> ResumeAsync(
        string tenantKey,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => resumeTenant.HandleAsync(new ResumeTenantCommand(tenantKey, reasonMessage, actorKey), ct);

    public ValueTask<UpdateTenantResponse> UpdateAsync(
        string tenantKey,
        int expectedVersion,
        string? displayName,
        string? description,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        updateTenant.HandleAsync(
            new UpdateTenantCommand(tenantKey, expectedVersion, displayName, description, reasonMessage, actorKey),
            ct
        );

    /// <summary>
    /// The key of the tenant with this id, null when no tenant has it. Known keys are cached for the process lifetime:
    /// a tenant's key never changes and its row is never deleted. A miss is read again on every call.
    /// </summary>
    internal ValueTask<GetTenantKeyResponse?> GetTenantKeyAsync(int tenantId, CancellationToken ct) =>
        getTenantKey.HandleAsync(new GetTenantKeyQuery(tenantId), ct);
}
