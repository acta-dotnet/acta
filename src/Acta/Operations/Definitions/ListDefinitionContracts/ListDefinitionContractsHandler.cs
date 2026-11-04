namespace Acta;

/// <summary><see cref="DefinitionsOperations.ListDefinitionContractsAsync"/>: one read.</summary>
internal sealed class ListDefinitionContractsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListDefinitionContractsResponse> HandleAsync(ListDefinitionContractsQuery query, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<ListDefinitionContractsRow>();
        var contracts = await session.QueryListAsync(
            "ListDefinitionContracts",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.NamespaceId, query.NamespaceId)),
            reader => read(reader).ToContract(),
            ct
        );
        return new ListDefinitionContractsResponse(contracts);
    }
}
