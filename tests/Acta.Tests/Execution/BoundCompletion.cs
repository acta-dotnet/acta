using Acta.Runtime;
using Acta.Tests.Runtime;

namespace Acta.Tests.Execution;

/// <summary>
/// One completion as the runner bound it for <c>complete_execution</c>, read back through the specs that bound it.
/// </summary>
internal sealed record BoundCompletion(
    bool Succeeded,
    EventReasonCode? EventReasonCode,
    string? ReasonMessage,
    byte? RescheduleStatusCode,
    int? RescheduleDelaySeconds,
    DateTime? RescheduleResumeAtUtc,
    byte? HandlerStatusCode,
    JobStatusCode? FinalStatus,
    int? FailureCount
)
{
    /// <summary>
    /// The attempt outcome the bound shape lands: a success, a handler's cancel or pause, a suspend, a budget-neutral
    /// re-arm (a re-arm with no failure count), and otherwise a failure, retried or terminal.
    /// </summary>
    public ExecutionOutcome Outcome =>
        this switch
        {
            { Succeeded: true } => ExecutionOutcome.Succeeded,
            { HandlerStatusCode: (byte)JobStatusCode.Cancelled } => ExecutionOutcome.Cancelled,
            { HandlerStatusCode: (byte)JobStatusCode.Paused } => ExecutionOutcome.Paused,
            { RescheduleStatusCode: (byte)ExecutionStatusCode.Suspended } => ExecutionOutcome.Suspended,
            { RescheduleStatusCode: not null, FailureCount: null } => ExecutionOutcome.Rescheduled,
            _ => ExecutionOutcome.Failed,
        };

    public static BoundCompletion Read(BoundCall call) =>
        new(
            call.Get(ActaSchema.Sql.ExecutionSucceeded),
            call.Get(ActaSchema.Event.ReasonCode),
            call.Get(ActaSchema.Event.ReasonMessage),
            call.Get(ActaSchema.Sql.RescheduleStatusCode),
            call.Get(ActaSchema.Sql.RescheduleDelaySeconds),
            call.Get(ActaSchema.Sql.RescheduleResumeAtUtc),
            call.Get(ActaSchema.Sql.HandlerStatusCode),
            (JobStatusCode?)call.Get(ActaSchema.Sql.FinalStatus),
            call.Get(ActaSchema.Sql.FailureCount)
        );
}
