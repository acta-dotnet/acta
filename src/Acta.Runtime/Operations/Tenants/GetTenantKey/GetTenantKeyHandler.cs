namespace Acta.Runtime;

/// <summary>Reads a tenant's key by its id; null when no tenant has that id.</summary>
internal sealed class GetTenantKeyHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<string?> HandleAsync(GetTenantKeyQuery query, CancellationToken ct) =>
        new(
            session.QuerySingleAsync(
                "GetTenantKey",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.TenantId, query.TenantId)),
                static reader => reader.GetString(0),
                ct
            )
        );
}
