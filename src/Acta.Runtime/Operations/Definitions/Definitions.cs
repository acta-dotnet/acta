namespace Acta.Runtime;

/// <summary><see cref="IDefinitions"/>, one line per operation.</summary>
internal sealed class Definitions(
    SetJobDefinitionOverridesHandler setJobDefinitionOverrides,
    RetireJobDefinitionHandler retireJobDefinition,
    GetJobDefinitionHandler getJobDefinition,
    ListJobDefinitionsHandler listJobDefinitions
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

    public ValueTask<RetireJobDefinitionResponse> RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        retireJobDefinition.HandleAsync(
            new RetireJobDefinitionCommand(jobNamespace, jobName, expectedVersion, reasonMessage, actorKey),
            ct
        );

    public ValueTask<DefinitionDetail?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default) =>
        getJobDefinition.HandleAsync(new GetJobDefinitionQuery(jobNamespace, jobName), ct);

    public ValueTask<PagedResult<DefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
        listJobDefinitions.HandleAsync(query, ct);
}
