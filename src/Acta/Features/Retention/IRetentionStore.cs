namespace Acta.Features.Retention;

/// <summary>
/// Persistence port for cross-feature expiration purging: one namespace-scoped
/// <c>purge_expired_data</c> sweep running five ordered bounded delete sections (terminal jobs past
/// retention, old events, settled alerts, dead workers, and a global expired-lock reap).
/// </summary>
internal interface IRetentionStore
{
    /// <summary>
    /// Runs one bounded purge pass and returns the per-section deleted counts. Batch and iteration
    /// caps bound each tick; the next fire continues any backlog.
    /// </summary>
    Task<PurgeExpiredDataResult> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct);
}

/// <summary>Validated purge sweep bounds for one namespace.</summary>
internal sealed record PurgeExpiredDataCommand(
    short NamespaceId,
    int EventsRetentionDays,
    int AlertRetentionDays,
    int WorkerRetentionSeconds,
    int BatchSize,
    int MaxIterations
);

/// <summary>
/// Per-section deleted counts from one <c>purge_expired_data</c> sweep.
/// </summary>
internal readonly record struct PurgeExpiredDataResult(int Jobs, int Events, int Alerts, int Workers, int Locks);
