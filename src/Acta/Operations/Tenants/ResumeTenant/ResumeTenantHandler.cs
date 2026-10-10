namespace Acta;

/// <summary><see cref="ITenants.ResumeAsync"/>: canonicalizes the name and stamps the operator; the SQL does the rest.</summary>
internal sealed class ResumeTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ResumeTenantResponse> HandleAsync(ResumeTenantCommand command, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(command.TenantName, "tenantName", NameSyntax.ExtendedMaxLength, reserveSystem: true);

        return await session.ExecuteOneAsync(
            new StoreCommand("ResumeTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantName, name));
                cmd.AddOperatorAudit(dialect, command.Actor, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<ResumeTenantResponse>(),
            ct
        );
    }
}
