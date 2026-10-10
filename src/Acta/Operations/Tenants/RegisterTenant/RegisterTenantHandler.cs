namespace Acta;

/// <summary><see cref="ITenants.RegisterAsync"/>: canonicalizes the key, checks the text fields, then one insert-or-get.</summary>
internal sealed class RegisterTenantHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<RegisterTenantResponse> HandleAsync(RegisterTenantCommand command, CancellationToken ct)
    {
        var key = NameSyntax.Canonicalize(command.TenantKey, "tenantKey", NameSyntax.ExtendedMaxLength, reserveSystem: true);
        CatalogValidation.ValidateTenant(command.DisplayName, command.Description);

        return await session.ExecuteOneAsync(
            new StoreCommand("RegisterTenant"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.TenantKey, key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.DisplayName, command.DisplayName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.Description, command.Description));
            },
            DbProjectionResolver.Resolve<RegisterTenantResponse>(),
            ct
        );
    }
}
