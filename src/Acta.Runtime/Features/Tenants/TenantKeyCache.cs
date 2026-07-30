using System.Collections.Concurrent;

namespace Acta.Features.Tenants;

/// <summary>
/// Process-lifetime <c>tenant_id -> tenant_key</c> cache for the execution path. Positive entries are
/// safe to cache forever: a tenant row's key is immutable and rows are never deleted. A miss (an id
/// this store does not know) is re-probed per call and never stored.
/// </summary>
internal sealed class TenantKeyCache(ITenantStore store)
{
    private readonly ConcurrentDictionary<int, string> _keys = new();

    public async ValueTask<string?> ResolveAsync(int tenantId, CancellationToken ct)
    {
        if (_keys.TryGetValue(tenantId, out var cached))
        {
            return cached;
        }

        var tenant = await store.GetTenantAsync(new TenantPointLookup(null, tenantId), ct);
        return tenant is null ? null : _keys.GetOrAdd(tenantId, tenant.TenantKey);
    }
}
