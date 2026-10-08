namespace Acta.Runtime;

/// <summary><see cref="IDefinitions"/>, one line per operation.</summary>
internal sealed class Definitions(
    SetDefinitionOverridesHandler setDefinitionOverrides,
    RetireDefinitionHandler retireDefinition,
    GetDefinitionHandler getDefinition,
    ListDefinitionsHandler listDefinitions
) : IDefinitions
{
    public ValueTask<SetDefinitionOverridesResponse> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        setDefinitionOverrides.HandleAsync(
            new SetDefinitionOverridesCommand(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actorKey),
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

    public ValueTask<GetDefinitionResponse?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default) =>
        getDefinition.HandleAsync(new GetDefinitionQuery(jobNamespace, jobName), ct);

    public ValueTask<ListDefinitionsResponse> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
        listDefinitions.HandleAsync(query, ct);
}
