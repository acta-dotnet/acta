namespace Acta.Runtime;

/// <summary><see cref="INamespaces.UpdateAsync"/>: checks the name and the text fields and stamps the operator; the SQL does the rest.</summary>
internal sealed class UpdateNamespaceHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<UpdateNamespaceResponse> HandleAsync(UpdateNamespaceCommand command, CancellationToken ct)
    {
        var name = IdentifierSyntax.CanonicalizeUserKebab(command.Name, "name", IdentifierSyntax.ExtendedMaxLength);
        CatalogValidation.ValidateNamespace(command.OwnerTeam, command.Description);

        return await session.ExecuteOneAsync(
            new StoreCommand("UpdateNamespace"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.OwnerTeam, command.OwnerTeam));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobNamespace.Description, command.Description));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, command.ExpectedVersion));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<UpdateNamespaceResponse>(),
            ct
        );
    }
}
