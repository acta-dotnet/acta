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
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => definitions.UpdateOverridesAsync(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actorKey, ct);

    public ValueTask<DefinitionControlResult> RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => definitions.RetireAsync(jobNamespace, jobName, expectedVersion, reasonMessage, actorKey, ct);

    public ValueTask<JobDefinitionDetail?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default) =>
        definitions.GetAsync(jobNamespace, jobName, ct);

    public ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
        definitions.ListAsync(query, ct);
}
