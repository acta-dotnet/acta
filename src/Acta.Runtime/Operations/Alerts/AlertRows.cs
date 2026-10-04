namespace Acta.Runtime;

/// <summary>
/// One alert-relevant <c>events</c> row projected for the <c>sys.alerts</c> generate phase, joined to
/// its definition's alert policy. The projector classifies the reason in C# from the
/// <c>(ExecutionStatus, ToStatus, ReasonCode)</c> triple, never from the mutable <c>runtimes.failure_count</c>.
/// Projection is a pure function of the event stream - identity comes from the deduplication key and
/// ordering from the event id - so no clock enters a classification decision.
/// <see cref="CreatedAtUtc"/> is carried for one purpose only, the projector's cursor, which walks the
/// stream by (stamp, id) because id order alone can invert against commit order.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record AlertableEvent(
    long EventId,
    DateTime CreatedAtUtc,
    long? JobId,
    int DefinitionId,
    string JobName,
    AlertProfileCode AlertProfile,
    string? AlertChannelName,
    ExecutionStatusCode? ExecutionStatus,
    JobStatusCode? ToStatus,
    EventReasonCode? ReasonCode,
    string? ReasonMessage
);

/// <summary>
/// One <c>alerts</c> row due for delivery - a first attempt, a due retry, or a due reminder for an
/// incident that is still open. Carries notification content and the logical channel name; transport
/// resolution happens against the worker's in-memory channel registry.
/// <see cref="Version"/> is the row's version at selection: settlement compares against it so an
/// attempt that raced a resolve (or another settlement) writes nothing instead of overwriting the
/// newer state. <see cref="Origin"/> decides whether settling this send schedules the next reminder:
/// an automatic alert tracks a condition Acta itself watches and re-notifies while it stays open; a
/// manual one belongs to the handler that raised it.
/// </summary>
[DbProjection(ByName = true)]
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
    int RetryCount,
    string ChannelName,
    Guid AlertRef,
    Guid? JobRef,
    int Version,
    AlertOriginCode Origin
);

/// <summary>
/// The row an alert control verb (acknowledge / resolve) answers: the action plus the alert's
/// <c>acknowledged_at_utc</c> / <c>resolved_at_utc</c> after the attempt. Bound by position: the
/// PostgreSQL routines name their columns with an <c>out_</c> prefix.
/// </summary>
[DbProjection]
internal sealed record AlertControlRow(ControlAction Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc);

/// <summary>
/// One alert raise's outcome: the row's post-raise <c>occurrence_count</c> and
/// <c>last_projected_event_id</c>. When the raise applied, the mark equals the command's
/// <c>SourceEventId</c> by definition; when the replay guard held the write back, both values are the
/// stored ones - which is how the projector tells the true threshold-crossing event (the mark still
/// names it) apart from a replayed neighbour whose raise the row had already absorbed.
/// </summary>
internal sealed record AlertOccurrence(int OccurrenceCount, long? LastProjectedEventId);
