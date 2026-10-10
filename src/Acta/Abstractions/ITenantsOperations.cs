namespace Acta;

/// <summary><see cref="ITenants"/> plus the tenant-key read the execution path runs.</summary>
internal interface ITenantsOperations : ITenants
{
    /// <summary>
    /// The key of the tenant with this id, null when no tenant has it. Known keys are cached for the process lifetime:
    /// a tenant's key never changes and its row is never deleted. A miss is read again on every call.
    /// </summary>
    ValueTask<GetTenantKeyResponse?> GetTenantKeyAsync(int tenantId, CancellationToken ct);
}
