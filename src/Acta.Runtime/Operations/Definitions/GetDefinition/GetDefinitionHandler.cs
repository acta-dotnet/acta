namespace Acta.Runtime;

/// <summary><see cref="IDefinitions.GetAsync"/>: resolves the natural key, then reads the row by id.</summary>
internal sealed class GetDefinitionHandler(DefinitionKeyResolution keys, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetDefinitionResponse?> HandleAsync(GetDefinitionQuery query, CancellationToken ct)
    {
        var definitionId = await keys.ResolveAsync(query.JobNamespace, query.JobName, ct);
        return definitionId is null
            ? null
            : await session.QuerySingleAsync(
                "GetDefinition",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.Id, definitionId.Value)),
                DbProjectionResolver.Resolve<GetDefinitionResponse>(),
                ct
            );
    }
}
