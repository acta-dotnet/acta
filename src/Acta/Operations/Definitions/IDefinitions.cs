namespace Acta;

/// <summary>
/// Job definitions domain: operator policy overrides plus the definition detail/list reads. Reached
/// through <see cref="IActaOperations.Definitions"/>.
/// </summary>
public interface IDefinitions
{
    /// <summary>Apply <paramref name="overrides"/> to the definition named by <paramref name="jobNamespace"/> and <paramref name="jobName"/>, guarded by <paramref name="expectedVersion"/>. Missing is NotFound; stale version is Rejected.</summary>
    ValueTask<SetDefinitionOverridesResponse> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Retire the definition named by <paramref name="jobNamespace"/> and <paramref name="jobName"/>, guarded by
    /// <paramref name="expectedVersion"/>: it moves to <see cref="DefinitionStatusCode.Retired"/>, its parked jobs
    /// (Ready, Blocked, Suspended, Paused) are cancelled in the same transaction without their descendants, a running
    /// attempt finishes, and enqueue is rejected with <see cref="EnqueueRejectionReason.DefinitionRetired"/>. A build
    /// carrying the definition at an equal or newer manifest generation makes it Active again on registration; an
    /// older one cannot. Missing is NotFound, a stale version Rejected, an already retired definition Applied.
    /// </summary>
    ValueTask<RetireDefinitionResponse> RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Read a single job definition's full detail by its natural key, or null when none matches.</summary>
    ValueTask<GetDefinitionResponse?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default);

    /// <summary>List job definitions ordered by namespace then name, optionally filtered by namespace and status.</summary>
    ValueTask<ListDefinitionsResponse> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default);
}
