using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// One alert-relevant <c>events</c> row projected for the <c>sys.alerts</c> generate phase, joined to
/// its definition's alert policy. The projector classifies the reason in C# from the
/// <c>(ExecutionStatus, ToStatus, ReasonCode)</c> triple, never from the mutable <c>runtimes.failure_count</c>.
/// <see cref="CreatedAtUtc"/> is the event's own write instant: the projector floors it into the
/// dedupe-window bucket, so a crash-replay re-derives the bucket from the event rather than from the
/// replaying pass's clock.
/// </summary>
internal sealed record AlertableEvent(
    long EventId,
    long? JobId,
    int DefinitionId,
    string JobName,
    AlertProfileCode AlertProfile,
    string? AlertChannelName,
    ExecutionStatusCode? ExecutionStatus,
    JobStatusCode? ToStatus,
    JobEventReasonCode? ReasonCode,
    string? ReasonMessage,
    DateTime CreatedAtUtc
);

/// <summary>
/// One <c>alerts</c> row due for delivery - a first attempt, a due retry, or a reminder for an
/// incident still open past the reminder interval. Carries notification content and the logical
/// channel name; transport resolution happens against the worker's in-memory channel registry.
/// <see cref="Version"/> is the row's version at selection: settlement compares against it so an
/// attempt that raced a resolve (or another settlement) writes nothing instead of overwriting the
/// newer state.
/// </summary>
internal sealed record DeliverableAlert(
    long AlertId,
    long? JobId,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string? RunbookUrl,
    int OccurrenceCount,
    DateTime CreatedAtUtc,
    byte RetryCount,
    string ChannelName,
    Guid AlertRef,
    Guid? JobRef,
    int Version
);

/// <summary>
/// Result of an alert control transition (acknowledge / resolve): the action plus the alert's
/// <c>acknowledged_at_utc</c> / <c>resolved_at_utc</c> after the attempt. Shared by both verbs, which
/// each return one <c>(action, acknowledged_at_utc, resolved_at_utc)</c> row.
/// </summary>
internal sealed record AlertControlOutcome(JobControlActionInternal Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc);

/// <summary>
/// Result of one alert raise: the row's post-raise <c>occurrence_count</c> and
/// <c>last_projected_event_id</c>. When the raise applied, the mark equals the command's
/// <c>SourceEventId</c> by definition; when the replay guard held the write back, both values are the
/// stored ones - which is how the projector tells the true threshold-crossing event (the mark still
/// names it) apart from a replayed neighbour whose raise the row had already absorbed.
/// </summary>
internal sealed record AlertRaiseOutcome(int OccurrenceCount, long? LastProjectedEventId);

/// <summary>
/// Flat alert list row in SELECT order; dedupe and channel-config columns are never selected.
/// </summary>
internal sealed record JobAlertListProjectionRow(
    long AlertId,
    string JobNamespace,
    long? JobId,
    AlertOriginCode Origin,
    AlertSeverityCode Severity,
    AlertKindCode Reason,
    string Title,
    string Message,
    string ChannelName,
    int OccurrenceCount,
    DateTime? ResolvedAtUtc,
    AlertDeliveryStatusCode DeliveryStatus,
    byte RetryCount,
    DateTime? RetryAfterUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    Guid? JobRef,
    DateTime? AcknowledgedAtUtc,
    Guid AlertRef
)
{
    public AlertListItem ToItem() =>
        new(
            new Acta.AlertRef(AlertRef),
            AlertId,
            JobNamespace,
            JobId,
            JobRef is { } jobRef ? new JobRef(jobRef) : null,
            Origin,
            Severity,
            Reason,
            Title,
            Message,
            ChannelName,
            OccurrenceCount,
            ResolvedAtUtc,
            DeliveryStatus,
            RetryCount,
            RetryAfterUtc,
            CreatedAtUtc,
            ModifiedAtUtc,
            AcknowledgedAtUtc
        );
}
