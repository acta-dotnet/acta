namespace Acta;

/// <summary><see cref="ITenants.GetAsync"/>: canonicalizes the name, then one point read.</summary>
internal sealed class GetTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<GetTenantResponse?> HandleAsync(GetTenantQuery query, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(query.TenantName, "tenantName", NameSyntax.ExtendedMaxLength);

        return new(
            session.QuerySingleAsync(
                "GetTenant",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantName, name)),
                DbProjectionResolver.Resolve<GetTenantResponse>(),
                ct
            )
        );
    }
}
