using System.Data.Common;

namespace Acta;

/// <summary><see cref="DefinitionsOperations.ListDefinitionContractsAsync(int, CancellationToken)"/>: one read.</summary>
internal sealed class ListDefinitionContractsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListDefinitionContractsResponse> HandleAsync(ListDefinitionContractsQuery query, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<ListDefinitionContractsRow>();
        // Inside a deploy's transaction the read sees the rows that deploy has written.
        var contracts = query.Transaction is { } transaction
            ? await session.ExecuteInTransactionAsync(transaction, new StoreCommand("ListDefinitionContracts"), Bind, Map, ct)
            : await session.QueryListAsync("ListDefinitionContracts", Bind, Map, ct);
        return new ListDefinitionContractsResponse(contracts);

        void Bind(DbCommand cmd) => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.NamespaceId, query.NamespaceId));
        StoredDefinitionContract Map(DbDataReader reader) => read(reader).ToContract();
    }
}
