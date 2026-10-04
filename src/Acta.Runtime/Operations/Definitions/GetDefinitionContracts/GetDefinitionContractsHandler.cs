namespace Acta.Runtime;

/// <summary>
/// The full catalog of every definition in a namespace, for worker startup and the policy reload tick:
/// identity, generation, hash, status, contract, modification time, and effective policy.
/// </summary>
internal sealed class GetDefinitionContractsHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<StoredDefinitionContract>> HandleAsync(GetDefinitionContractsQuery query, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<GetDefinitionContractsRow>();
        return session.QueryListAsync(
            "GetDefinitionContracts",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.NamespaceId, query.NamespaceId)),
            reader => read(reader).ToContract(),
            ct
        );
    }
}
