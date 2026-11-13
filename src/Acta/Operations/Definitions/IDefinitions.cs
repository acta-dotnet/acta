namespace Acta;

/// <summary>
/// Job definitions domain: operator policy overrides plus the definition detail/list reads. Reached
/// through <see cref="IActaOperations.Definitions"/>.
/// </summary>
public interface IDefinitions
{
    /// <summary>Apply <paramref name="overrides"/> to the definition named by <paramref name="jobNamespace"/> and <paramref name="jobName"/>, guarded by <paramref name="expectedVersion"/>. Missing is NotFound; a stale version is VersionConflict.</summary>
    ValueTask<UpdateDefinitionOverridesResponse> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        long expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Retire the definition named by <paramref name="jobNamespace"/> and <paramref name="jobName"/>, guarded by
    /// <paramref name="expectedVersion"/>: it moves to <see cref="DefinitionStatusCode.Retired"/>, enqueue is rejected with
    /// <see cref="EnqueueRejectionReason.DefinitionRetired"/>, its parked jobs (Ready, Blocked, Suspended, Paused) are cancelled in
    /// the same transaction without their descendants, and a running attempt finishes but its job lands Cancelled rather than run
    /// again. A registration that declares the definition makes it Active again. Missing is NotFound, a stale version
    /// VersionConflict, an already retired definition Applied.
    /// </summary>
    ValueTask<RetireDefinitionResponse> RetireAsync(
        string jobNamespace,
        string jobName,
        long expectedVersion,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>Read a single job definition's full detail by its natural key, or null when none matches.</summary>
    ValueTask<GetDefinitionResponse?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default);

    /// <summary>
    /// Read how long a definition's executions took over a range and how many failed, per interval and as a duration
    /// histogram, or null when no definition matches. It counts the events the ledger still holds, so a range whose
    /// events retention has removed reads empty, and a definition that records failures only shows few successes. A
    /// range outside the query's bounds throws <see cref="InvalidQueryException"/>.
    /// </summary>
    ValueTask<GetDefinitionPerformanceResponse?> GetPerformanceAsync(GetDefinitionPerformanceQuery query, CancellationToken ct = default);

    /// <summary>List job definitions ordered by namespace then name, optionally filtered by namespace and status.</summary>
    ValueTask<ListDefinitionsResponse> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default);

    /// <summary>
    /// The input contract of a registered job, read from the in-process generated manifest: no
    /// database round-trip, no reflection. Returns null when this host has not registered the job's
    /// manifest (an enqueue-only dashboard pointed at a shared ledger), so callers degrade quietly. The key is trimmed and
    /// folded to lowercase as every name is, and one that is no name throws <see cref="InvalidQueryException"/>.
    /// </summary>
    ValueTask<GetDefinitionInputTemplateResponse?> GetInputTemplateAsync(
        string jobNamespace,
        string jobName,
        CancellationToken ct = default
    );
}
