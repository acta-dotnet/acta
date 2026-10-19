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
