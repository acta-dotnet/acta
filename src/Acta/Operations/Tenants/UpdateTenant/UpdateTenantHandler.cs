namespace Acta;

/// <summary>
/// <see cref="ITenants.UpdateAsync"/>: canonicalizes the key, checks the text fields, and stamps the
/// operator; the SQL does the rest.
/// </summary>
internal sealed class UpdateTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<UpdateTenantResponse> HandleAsync(UpdateTenantCommand command, CancellationToken ct)
    {
        var key = IdentifierSyntax.NormalizeTenantKey(command.TenantKey, "tenantKey");
        CatalogValidation.ValidateTenant(command.DisplayName, command.Description);

        return await session.ExecuteOneAsync(
            new StoreCommand("UpdateTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.DisplayName, command.DisplayName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, command.ExpectedVersion));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<UpdateTenantResponse>(),
            ct
        );
    }
}
