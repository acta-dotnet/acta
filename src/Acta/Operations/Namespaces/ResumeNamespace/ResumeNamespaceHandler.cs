namespace Acta;

/// <summary><see cref="INamespaces.ResumeAsync"/>: checks the name and stamps the operator; the SQL does the rest.</summary>
internal sealed class ResumeNamespaceHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ResumeNamespaceResponse> HandleAsync(ResumeNamespaceCommand command, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(command.Name, "name", NameSyntax.ExtendedMaxLength, reserveSystem: true);

        return await session.ExecuteOneAsync(
            new StoreCommand("ResumeNamespace"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, name));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<ResumeNamespaceResponse>(),
            ct
        );
    }
}
