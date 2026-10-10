namespace Acta;

/// <summary>
/// <see cref="ITenants.UpdateAsync"/>: canonicalizes the name, checks the text fields, and stamps the
/// operator; the SQL does the rest.
/// </summary>
internal sealed class UpdateTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<UpdateTenantResponse> HandleAsync(UpdateTenantCommand command, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(command.TenantName, "tenantName", NameSyntax.ExtendedMaxLength, reserveSystem: true);
        CatalogValidation.ValidateTenant(command.DisplayName, command.Description);

        return await session.ExecuteOneAsync(
            new StoreCommand("UpdateTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantName, name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.DisplayName, command.DisplayName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, command.ExpectedVersion));
                cmd.AddOperatorAudit(dialect, command.Actor, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<UpdateTenantResponse>(),
            ct
        );
    }
}
