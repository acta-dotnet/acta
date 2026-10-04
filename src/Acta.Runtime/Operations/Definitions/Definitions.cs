namespace Acta.Runtime;

/// <summary><see cref="IDefinitions"/>, one line per operation.</summary>
internal sealed class Definitions(
    SetJobDefinitionOverridesHandler setJobDefinitionOverrides,
    RetireDefinitionHandler retireDefinition,
    GetDefinitionHandler getDefinition,
    ListDefinitionsHandler listDefinitions
) : IDefinitions
{
    public ValueTask<SetJobDefinitionOverridesResponse> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        setJobDefinitionOverrides.HandleAsync(
            new SetJobDefinitionOverridesCommand(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actorKey),
            ct
        );

    public ValueTask<RetireDefinitionResponse> RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => retireDefinition.HandleAsync(new RetireDefinitionCommand(jobNamespace, jobName, expectedVersion, reasonMessage, actorKey), ct);

    public ValueTask<DefinitionDetail?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default) =>
        getDefinition.HandleAsync(new GetDefinitionQuery(jobNamespace, jobName), ct);

    public ValueTask<PagedResult<DefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
        listDefinitions.HandleAsync(query, ct);
}
