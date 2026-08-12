namespace Acta.Runtime.Modules.Execution;

/// <summary>Coarse outcome the worker reports back after handler invocation.</summary>
internal enum ExecutionOutcome : byte
{
    /// <summary>Handler returned normally. Terminal status <c>Succeeded</c> (100).</summary>
    Succeeded = 1,

    /// <summary>Handler threw. The failure budget at completion decides re-arm to <c>Ready</c> versus
    /// terminal <c>Failed</c> (200).</summary>
    Failed = 2,

    /// <summary>Handler raised <c>RescheduleJobException</c>. The Job re-arms to <c>Ready</c> with a
    /// forward-dated <c>NextRunAtUtc</c>; budget-neutral.</summary>
    Rescheduled = 3,

    /// <summary>Handler suspended via <c>ctx.SleepAsync</c>. The Job re-arms to <c>Ready</c> with the
    /// sleep timer's due instant; budget-neutral.</summary>
    Suspended = 4,

    /// <summary>Handler called <c>ctx.CancelAsync</c>. Deliberate terminal status <c>Cancelled</c>
    /// (220); not retried, budget untouched.</summary>
    Cancelled = 5,

    /// <summary>Handler called <c>ctx.PauseAsync</c>. The Job is held at <c>Paused</c> (30) until an
    /// external resume; not retried, no <c>next_run_at_utc</c>, budget untouched.</summary>
    Paused = 6,
}
