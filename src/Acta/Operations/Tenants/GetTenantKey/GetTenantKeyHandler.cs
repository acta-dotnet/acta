using System.Collections.Concurrent;

namespace Acta;

/// <summary><see cref="TenantsOperations.GetTenantKeyAsync"/>: one point read per known tenant, then the cache.</summary>
internal sealed class GetTenantKeyHandler(IDbSession session, ISqlDialect dialect)
{
    private readonly ConcurrentDictionary<int, GetTenantKeyResponse> _keys = new();

    public ValueTask<GetTenantKeyResponse?> HandleAsync(GetTenantKeyQuery query, CancellationToken ct) =>
        _keys.TryGetValue(query.TenantId, out var cached) ? new ValueTask<GetTenantKeyResponse?>(cached) : ReadAsync(query.TenantId, ct);

    private async ValueTask<GetTenantKeyResponse?> ReadAsync(int tenantId, CancellationToken ct) =>
        await session.QuerySingleAsync(
            "GetTenantKey",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.TenantId, tenantId)),
            static reader => reader.GetString(0),
            ct
        )
            is { } tenantKey
            ? _keys.GetOrAdd(tenantId, new GetTenantKeyResponse(tenantKey))
            : null;
}
