namespace Acta;

/// <summary>
/// Alerts domain: the operator acknowledge/resolve verbs plus the keyset-paginated alert list. Reached
/// through <see cref="IActaOperations.Alerts"/>.
/// </summary>
public interface IAlerts
{
    /// <summary>
    /// Acknowledge the alert identified by <paramref name="alertId"/>: sets <c>AcknowledgedAtUtc</c> to
    /// now and emits <c>alert.acknowledged</c> always, regardless of the alert's job's audit level,
    /// since this is low-volume operator activity. Re-acknowledging an already-acknowledged alert is
    /// <see cref="ControlAction.Applied"/> without mutation: the existing timestamp is returned and
    /// no second event is emitted. A missing alert is <see cref="ControlAction.NotFound"/>.
    /// <paramref name="reasonMessage"/> is folded into the audit event's reason message alongside the alert id;
    /// <paramref name="actorKey"/> is recorded on the audit event as the operator identity (e.g. the
    /// authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<AlertControlResult> AcknowledgeAsync(
        long alertId,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Manually resolve the alert identified by <paramref name="alertId"/>: sets
    /// <c>ResolvedAtUtc</c> to now and emits <c>alert.resolved</c> always, regardless of the alert's
    /// job's audit level. Does not require a prior acknowledge. Re-resolving an already-resolved alert
    /// is <see cref="ControlAction.Applied"/> without mutation: the existing timestamp is returned
    /// and no second event is emitted. A missing alert is <see cref="ControlAction.NotFound"/>.
    /// <paramref name="reasonMessage"/> is folded into the audit event's reason message alongside the alert id;
    /// <paramref name="actorKey"/> is recorded on the audit event as the operator identity (e.g. the
    /// authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<AlertControlResult> ResolveAsync(
        long alertId,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// List alerts newest first, optionally filtered by namespace, job, resolution, acknowledgement,
    /// severity, and delivery status.
    /// </summary>
    ValueTask<PagedResult<AlertListItem>> ListAsync(ListAlertsQuery query, CancellationToken ct = default);

    /// <summary>Point-read one alert by id; null when it does not exist.</summary>
    ValueTask<AlertDetail?> GetAsync(long alertId, CancellationToken ct = default);
}
