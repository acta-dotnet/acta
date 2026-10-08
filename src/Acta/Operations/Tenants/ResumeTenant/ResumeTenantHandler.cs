namespace Acta;

/// <summary><see cref="ITenants.ResumeAsync"/>: canonicalizes the key and stamps the operator; the SQL does the rest.</summary>
internal sealed class ResumeTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ResumeTenantResponse> HandleAsync(ResumeTenantCommand command, CancellationToken ct)
    {
        var key = IdentifierSyntax.NormalizeTenantKey(command.TenantKey, "tenantKey");

        return await session.ExecuteOneAsync(
            new StoreCommand("ResumeTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, key));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<ResumeTenantResponse>(),
            ct
        );
    }
}
