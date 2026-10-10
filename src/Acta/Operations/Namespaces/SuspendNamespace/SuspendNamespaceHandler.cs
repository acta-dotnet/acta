namespace Acta;

/// <summary><see cref="INamespaces.SuspendAsync"/>: checks the name and stamps the operator; the SQL does the rest.</summary>
internal sealed class SuspendNamespaceHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<SuspendNamespaceResponse> HandleAsync(SuspendNamespaceCommand command, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(command.Name, "name", NameSyntax.ExtendedMaxLength, reserveSystem: true);

        return await session.ExecuteOneAsync(
            new StoreCommand("SuspendNamespace"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, name));
                cmd.AddOperatorAudit(dialect, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<SuspendNamespaceResponse>(),
            ct
        );
    }
}
