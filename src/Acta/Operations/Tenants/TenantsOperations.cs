namespace Acta;

/// <summary>The Tenants operations: <see cref="ITenants"/> for applications, plus the engine's calls.</summary>
internal sealed class TenantsOperations(
    RegisterTenantHandler registerTenant,
    GetTenantHandler getTenant,
    ListTenantsHandler listTenants,
    SuspendTenantHandler suspendTenant,
    ResumeTenantHandler resumeTenant,
    UpdateTenantHandler updateTenant,
    GetTenantNameHandler getTenantName
) : ITenants
{
    public ValueTask<RegisterTenantResponse> RegisterAsync(
        string tenantName,
        string? displayName = null,
        string? description = null,
        CancellationToken ct = default
    ) => registerTenant.HandleAsync(new RegisterTenantCommand(tenantName, displayName, description), ct);

    public ValueTask<GetTenantResponse?> GetAsync(string tenantName, CancellationToken ct = default) =>
        getTenant.HandleAsync(new GetTenantQuery(tenantName), ct);

    public ValueTask<ListTenantsResponse> ListAsync(ListTenantsQuery query, CancellationToken ct = default) =>
        listTenants.HandleAsync(query, ct);

    public ValueTask<SuspendTenantResponse> SuspendAsync(
        string tenantName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => suspendTenant.HandleAsync(new SuspendTenantCommand(tenantName, reasonMessage, actor), ct);

    public ValueTask<ResumeTenantResponse> ResumeAsync(
        string tenantName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => resumeTenant.HandleAsync(new ResumeTenantCommand(tenantName, reasonMessage, actor), ct);

    public ValueTask<UpdateTenantResponse> UpdateAsync(
        string tenantName,
        int expectedVersion,
        string? displayName,
        string? description,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => updateTenant.HandleAsync(new UpdateTenantCommand(tenantName, expectedVersion, displayName, description, reasonMessage, actor), ct);

    /// <summary>
    /// The name of the tenant with this id, null when no tenant has it. Known names are cached for the process lifetime:
    /// a tenant's name never changes and its row is never deleted. A miss is read again on every call.
    /// </summary>
    internal ValueTask<GetTenantNameResponse?> GetTenantNameAsync(int tenantId, CancellationToken ct) =>
        getTenantName.HandleAsync(new GetTenantNameQuery(tenantId), ct);
}
