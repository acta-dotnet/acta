namespace Acta;

/// <summary>
/// Job definitions domain: operator policy overrides plus the definition detail/list reads. Reached
/// through <see cref="IActaOperations.Definitions"/>.
/// </summary>
public interface IDefinitions
{
    /// <summary>Apply <paramref name="overrides"/> to the definition named by <paramref name="jobNamespace"/> and <paramref name="jobName"/>, guarded by <paramref name="expectedVersion"/>. Missing is NotFound; stale version is Rejected.</summary>
    ValueTask<DefinitionControlResult> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        JobDefinitionPolicyOverrides overrides,
        string? actorKey = null,
        string? reasonMessage = null,
        CancellationToken ct = default
    );

    /// <summary>Read a single job definition's full detail by its natural key, or null when none matches.</summary>
    ValueTask<JobDefinitionDetail?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default);

    /// <summary>List job definitions ordered by namespace then name, optionally filtered by namespace and status.</summary>
    ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default);
}
