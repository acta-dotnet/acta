namespace Acta;

/// <summary>
/// The full catalog of every definition in a namespace, for worker startup and the policy reload tick:
/// identity, generation, hash, status, contract, modification time, and effective policy.
/// </summary>
internal sealed class GetDefinitionContractsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetDefinitionContractsResponse> HandleAsync(GetDefinitionContractsQuery query, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<GetDefinitionContractsRow>();
        var contracts = await session.QueryListAsync(
            "GetDefinitionContracts",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.NamespaceId, query.NamespaceId)),
            reader => read(reader).ToContract(),
            ct
        );
        return new GetDefinitionContractsResponse(contracts);
    }
}
