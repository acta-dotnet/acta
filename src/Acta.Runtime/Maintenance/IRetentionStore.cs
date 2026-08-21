namespace Acta.Runtime.Maintenance;

/// <summary>
/// Persistence port for cross-feature expiration purging: one namespace-scoped
/// <c>purge_expired_data</c> sweep running seven ordered bounded delete sections (terminal jobs past
/// retention, old events, settled alerts, undelivered alerts past the same window, the projector's
/// aged poison-skip variables, terminal workers, and a global expired-lock reap).
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
    int AlertRetention,
    int WorkerRetentionSeconds,
    int BatchSize,
    int MaxIterations
);

/// <summary>
/// Per-section deleted counts from one <c>purge_expired_data</c> sweep.
/// <see cref="UndeliveredAlertsPurged"/> is counted apart from <see cref="Alerts"/> because an alert
/// aged out before delivery settled is a lost operator signal, not routine housekeeping.
/// </summary>
internal readonly record struct PurgeExpiredDataResult(
    int Jobs,
    int Events,
    int Alerts,
    int UndeliveredAlertsPurged,
    int Workers,
    int Locks
);
