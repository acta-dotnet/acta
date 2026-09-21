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

    /// <summary>
    /// Retire the definition named by <paramref name="jobNamespace"/> and <paramref name="jobName"/>,
    /// guarded by <paramref name="expectedVersion"/>. The definition moves to
    /// <see cref="JobDefinitionStatusCode.Retired"/> and its parked jobs (Ready, Suspended, Paused) are
    /// cancelled in the same transaction; a running attempt is left to finish. Descendants of a
    /// cancelled job are not cancelled with it. Enqueue is rejected from here on with
    /// <see cref="EnqueueRejectionReason.DefinitionRetired"/>. Registration by a build that carries the
    /// definition at an equal or newer manifest generation makes it Active again, with the jobs it
    /// still has; a build older than the stored generation cannot. Missing is NotFound; a stale
    /// <paramref name="expectedVersion"/> is Rejected; retiring an already retired definition is
    /// Applied and cancels nothing more.
    /// </summary>
    ValueTask<DefinitionControlResult> RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? actorKey = null,
        string? reasonMessage = null,
        CancellationToken ct = default
    );

    /// <summary>Read a single job definition's full detail by its natural key, or null when none matches.</summary>
    ValueTask<JobDefinitionDetail?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default);

    /// <summary>List job definitions ordered by namespace then name, optionally filtered by namespace and status.</summary>
    ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default);
}
