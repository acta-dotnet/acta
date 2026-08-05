using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ExecutionStatusCodeJsonConverter))]
[CodeKind("execution-status")]
public enum ExecutionStatusCode : byte
{
    [Code("executing", "Handler in flight.")]
    Executing = 50,

    [Code("succeeded", "Handler returned successfully. Resets the runtime failure count.")]
    Succeeded = 100,

    [Code("rescheduled", "Handler threw RescheduleJobException; does not charge budget.")]
    Rescheduled = 150,

    [Code(
        "suspended",
        "The attempt ended because the job parked: a ctx.SleepAsync timer, an awaited signal, or awaited children. Does not charge budget."
    )]
    Suspended = 151,

    [Code("paused", "Handler threw PauseJobException.")]
    Paused = 152,

    [Code("failed", "Handler threw or ctx.Fail called. Charges failure budget.")]
    Failed = 200,

    [Code("cancelled", "Handler ended via cancel; does not charge budget.")]
    Cancelled = 220,

    [Code("orphaned", "Lease lapsed past grace; reclaimed by the sys.recovery system job. Charges budget.")]
    Orphaned = 230,
}

/// <summary>Explicit runtime behavior of a completed or live execution attempt.</summary>
public enum ExecutionBehavior : byte
{
    Live = 1,
    Success = 2,
    Controlled = 3,
    Failure = 4,
    Cancelled = 5,
    Indeterminate = 6,
}

public static partial class ExecutionStatusExtensions
{
    extension(ExecutionStatusCode value)
    {
        public bool IsTerminal =>
            value
                is ExecutionStatusCode.Succeeded
                    or ExecutionStatusCode.Rescheduled
                    or ExecutionStatusCode.Suspended
                    or ExecutionStatusCode.Paused
                    or ExecutionStatusCode.Failed
                    or ExecutionStatusCode.Cancelled
                    or ExecutionStatusCode.Orphaned;

        public bool ChargesFailureBudget => value is ExecutionStatusCode.Failed or ExecutionStatusCode.Orphaned;

        public ExecutionBehavior GetBehavior() =>
            value switch
            {
                ExecutionStatusCode.Executing => ExecutionBehavior.Live,
                ExecutionStatusCode.Succeeded => ExecutionBehavior.Success,
                ExecutionStatusCode.Rescheduled or ExecutionStatusCode.Suspended or ExecutionStatusCode.Paused =>
                    ExecutionBehavior.Controlled,
                ExecutionStatusCode.Failed => ExecutionBehavior.Failure,
                ExecutionStatusCode.Cancelled => ExecutionBehavior.Cancelled,
                ExecutionStatusCode.Orphaned => ExecutionBehavior.Indeterminate,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
            };
    }
}
