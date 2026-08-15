namespace Acta.Runtime.Modules.Execution.Definitions;

/// <summary>
/// <see cref="IDefinitions"/> implementation: a thin adapter over the Definitions feature service,
/// which owns validation, actor stamping, and the store delegation.
/// </summary>
internal sealed class DefinitionsApi(DefinitionsService definitions) : IDefinitions
{
    public ValueTask<DefinitionControlResult> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        string? actorKey = null,
        string? reasonMessage = null,
        CancellationToken ct = default
    ) => definitions.UpdateOverridesAsync(jobNamespace, jobName, expectedVersion, overrides, actorKey, reasonMessage, ct);

    public ValueTask<JobDefinitionDetail?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default) =>
        definitions.GetAsync(jobNamespace, jobName, ct);

    public ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
        definitions.ListAsync(query, ct);
}
