namespace Acta;

/// <summary><see cref="IDefinitions.GetAsync"/>: resolves the natural key, then reads the row by id.</summary>
internal sealed class GetDefinitionHandler(GetDefinitionIdHandler getDefinitionId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetDefinitionResponse?> HandleAsync(GetDefinitionQuery query, CancellationToken ct) =>
        await getDefinitionId.HandleAsync(new GetDefinitionIdQuery(query.JobNamespace, query.JobName), ct)
            is { DefinitionId: var definitionId }
            ? await session.QuerySingleAsync(
                "GetDefinition",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DefinitionId, definitionId)),
                DbProjectionResolver.Resolve<GetDefinitionResponse>(),
                ct
            )
            : null;
}
