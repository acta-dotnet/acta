namespace Acta;

/// <summary><see cref="INamespaces.GetAsync"/>: canonicalizes the name, then one point read.</summary>
internal sealed class GetNamespaceHandler(IDbSession session, ISqlDialect dialect)
{
    public ValueTask<GetNamespaceResponse?> HandleAsync(GetNamespaceQuery query, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(query.NamespaceName, "jobNamespace");

        return new(
            session.QuerySingleAsync(
                "GetNamespace",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceName, name)),
                DbProjectionResolver.Resolve<GetNamespaceResponse>(),
                ct
            )
        );
    }
}
