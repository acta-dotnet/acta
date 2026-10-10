namespace Acta;

/// <summary>
/// One alert-relevant <c>events</c> row projected for the <c>sys.alerts</c> generate phase, joined to its definition's
/// alert policy. The projector classifies the reason in C# from the <c>(ExecutionStatus, ToStatus, ReasonCode)</c>
/// triple, never from the mutable <c>runtimes.failure_count</c>: projection is a pure function of the event stream,
/// identity from the deduplication key and order from the event id, so no clock enters a classification.
/// <see cref="CreatedAtUtc"/> serves the projector's cursor alone, which walks by (stamp, id) because id order can
/// invert against commit order.
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
/// One <c>alerts</c> row due for delivery: a first attempt, a due retry, or a due reminder for an incident still open.
/// Carries the notification content and the logical channel name; the transport resolves against the worker's
/// in-memory channel registry. <see cref="Version"/> is the row's version at selection, so a settlement that raced a
/// resolve or another settlement writes nothing. <see cref="Origin"/> decides whether settling this send schedules
/// the next reminder: an automatic alert tracks a condition Acta watches and re-notifies while it stays open; a manual
/// one belongs to the handler that raised it.
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
