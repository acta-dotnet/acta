namespace Acta;

/// <summary><see cref="ITenants.SuspendAsync"/>: canonicalizes the name and stamps the operator; the SQL does the rest.</summary>
internal sealed class SuspendTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<SuspendTenantResponse> HandleAsync(SuspendTenantCommand command, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(command.TenantName, "tenantName", NameSyntax.ExtendedMaxLength, reserveSystem: true);

        return await session.ExecuteOneAsync(
            new StoreCommand("SuspendTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantName, name));
                cmd.AddOperatorAudit(dialect, command.Actor, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<SuspendTenantResponse>(),
            ct
        );
    }
}
