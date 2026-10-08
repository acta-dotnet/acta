namespace Acta;

/// <summary><see cref="ITenants.GetAsync"/>: canonicalizes the key, then one point read.</summary>
internal sealed class GetTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<GetTenantResponse?> HandleAsync(GetTenantQuery query, CancellationToken ct)
    {
        var key = IdentifierSyntax.NormalizeTenantKey(query.TenantKey, "tenantKey");

        return new(
            session.QuerySingleAsync(
                "GetTenant",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, key)),
                DbProjectionResolver.Resolve<GetTenantResponse>(),
                ct
            )
        );
    }
}
