using Acta.Querying;

namespace Acta.Features.Definitions;

/// <summary>
/// <see cref="IDefinitions"/> implementation: a thin adapter over the Definitions feature service,
/// which owns validation, actor stamping, and the store delegation.
/// </summary>
internal sealed class DefinitionsApi(DefinitionsService definitions) : IDefinitions
{
    public ValueTask<DefinitionOverrideResult> SetOverridesAsync(
        int definitionId,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        string? actorKey = null,
        string? note = null,
        CancellationToken ct = default
    ) => definitions.SetOverridesAsync(definitionId, expectedVersion, overrides, actorKey, note, ct);

    public ValueTask<JobDefinitionDetail?> GetAsync(int definitionId, CancellationToken ct = default) =>
        definitions.GetAsync(definitionId, ct);

    public ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListJobDefinitionsQuery query, CancellationToken ct = default) =>
        definitions.ListAsync(query, ct);
}
