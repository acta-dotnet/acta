using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ExecutionStatusCodeJsonConverter))]
[CodeKind("execution-status")]
public enum ExecutionStatusCode : byte
{
    [Code("unspecified", "Execution status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

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

    [Code(
        "failed",
        "Handler threw or ctx.Fail called. A retry within budget charges the failure budget; ctx.Fail and a non-retryable exception end the job without charging it."
    )]
    Failed = 200,

    [Code("cancelled", "Handler ended via cancel; does not charge budget.")]
    Cancelled = 220,

    [Code("orphaned", "Lease lapsed past grace; reclaimed by crash recovery and charged to the failure budget.")]
    Orphaned = 230,
}
