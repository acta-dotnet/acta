namespace Acta.Runtime.Maintenance;

/// <summary>
/// Persistence port for one atomic, bounded batch of a retention section.
/// </summary>
internal interface IRetentionStore
{
    /// <summary>
    /// Deletes at most BatchSize eligible roots in the selected section, including their dependents.
    /// A failed batch rolls back; earlier successful batches belong to the sweep coordinator.
    /// </summary>
    Task<int> PurgeBatchAsync(PurgeExpiredDataBatchCommand command, CancellationToken ct);
}

/// <summary>Transient command selectors, not persisted codes. Order is the retention sweep order.</summary>
internal enum RetentionSection
{
    Jobs = 1,
    Events = 2,
    SettledAlerts = 3,
    UndeliveredAlerts = 4,
    PoisonSkipCheckpoints = 5,
    Workers = 6,
    Locks = 7,
}

internal sealed record PurgeExpiredDataBatchCommand(int NamespaceId, RetentionSection Section, DateTime CutoffUtc, int BatchSize);

/// <summary>Validated purge sweep bounds for one namespace.</summary>
internal sealed record PurgeExpiredDataCommand(
    int NamespaceId,
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
