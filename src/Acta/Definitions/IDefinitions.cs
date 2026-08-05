namespace Acta;

/// <summary>
/// Job definitions domain: operator policy overrides plus the definition detail/list reads. Reached
/// through <see cref="IActaOperations.Definitions"/>.
/// </summary>
public interface IDefinitions
{
    /// <summary>Apply <paramref name="overrides"/> to the definition <paramref name="jobDefinitionId"/>, guarded by <paramref name="expectedVersion"/>. Missing is NotFound; stale version is Rejected.</summary>
    ValueTask<DefinitionOverrideResult> UpdateOverridesAsync(
        int jobDefinitionId,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        string? actorKey = null,
        string? note = null,
        CancellationToken ct = default
    );

    /// <summary>Read a single job definition's full detail by surrogate id, or null when none matches.</summary>
    ValueTask<JobDefinitionDetail?> GetAsync(int jobDefinitionId, CancellationToken ct = default);

    /// <summary>List job definitions ordered by namespace then name, optionally filtered by namespace and status.</summary>
    ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListJobDefinitionsQuery query, CancellationToken ct = default);
}
