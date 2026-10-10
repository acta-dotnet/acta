namespace Acta;

/// <summary><see cref="ITenants.SuspendAsync"/>: canonicalizes the key and stamps the operator; the SQL does the rest.</summary>
internal sealed class SuspendTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<SuspendTenantResponse> HandleAsync(SuspendTenantCommand command, CancellationToken ct)
    {
        var key = NameSyntax.Canonicalize(command.TenantKey, "tenantKey", NameSyntax.ExtendedMaxLength, reserveSystem: true);

        return await session.ExecuteOneAsync(
            new StoreCommand("SuspendTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, key));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<SuspendTenantResponse>(),
            ct
        );
    }
}
