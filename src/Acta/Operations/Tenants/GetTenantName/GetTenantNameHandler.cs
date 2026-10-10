using System.Collections.Concurrent;

namespace Acta;

/// <summary><see cref="TenantsOperations.GetTenantNameAsync"/>: one point read per known tenant, then the cache.</summary>
internal sealed class GetTenantNameHandler(IDbSession session, ISqlDialect dialect)
{
    private readonly ConcurrentDictionary<int, GetTenantNameResponse> _names = new();

    public ValueTask<GetTenantNameResponse?> HandleAsync(GetTenantNameQuery query, CancellationToken ct) =>
        _names.TryGetValue(query.TenantId, out var cached) ? new ValueTask<GetTenantNameResponse?>(cached) : ReadAsync(query.TenantId, ct);

    private async ValueTask<GetTenantNameResponse?> ReadAsync(int tenantId, CancellationToken ct) =>
        await session.QuerySingleAsync(
            "GetTenantName",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.TenantId, tenantId)),
            static reader => reader.GetString(0),
            ct
        )
            is { } tenantName
            ? _names.GetOrAdd(tenantId, new GetTenantNameResponse(tenantName))
            : null;
}
