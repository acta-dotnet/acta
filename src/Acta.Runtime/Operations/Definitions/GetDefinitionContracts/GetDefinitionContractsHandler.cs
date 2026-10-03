namespace Acta.Runtime;

/// <summary>
/// The full catalog of every definition in a namespace, for worker startup and the policy reload tick:
/// identity, generation, hash, status, contract, modification time, and effective policy.
/// </summary>
internal sealed class GetDefinitionContractsHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<StoredDefinitionContract>> HandleAsync(GetDefinitionContractsQuery query, CancellationToken ct) =>
        session.QueryAsync<IReadOnlyList<StoredDefinitionContract>>(
            "GetDefinitionContracts",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobDefinition.NamespaceId, query.NamespaceId)),
            static async (reader, token) =>
            {
                var read = DbProjectionResolver.Resolve<GetDefinitionContractsRow>();
                var contracts = new List<StoredDefinitionContract>();
                while (await reader.ReadAsync(token))
                {
                    contracts.Add(read(reader).ToContract());
                }

                return contracts;
            },
            ct
        );
}
