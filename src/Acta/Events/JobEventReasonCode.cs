using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Stable family-local reason identifiers. Nearby values improve catalog readability but carry no
/// runtime subject, subgroup, or outcome semantics. Detailed diagnostics remain structured event data.
/// </summary>
[JsonConverter(typeof(JobEventReasonCodeJsonConverter))]
[CodeKind("job-event-reason")]
public enum JobEventReasonCode : byte
{
    // ---------- Job transitions ----------

    /// <summary>
    /// Default catch-all when no system-catalog code fits. The operator-readable story lives in
    /// <c>ReasonMessage</c>.
    /// </summary>
    [Code("job.other", "None of the system-catalog codes fit; the operator-readable story lives in ReasonMessage.")]
    Other = 10,

    [Code("job.unhandled-exception", "Handler threw an exception that was not raised via ctx.Fail.")]
    JobUnhandledException = 20,

    [Code("job.lease-expired", "Worker lease expired; the sys.recovery system job reclaimed the Job.")]
    JobLeaseExpired = 21,

    [Code("job.execution-timeout", "Execution exceeded JobDefinition.ExecutionTimeout for this attempt.")]
    JobExecutionTimeout = 22,

    [Code(
        "job.non-retryable-exception",
        "Handler threw an exception the runtime classifies as non-retryable (NotImplementedException, NotSupportedException); terminal Failed without consuming the retry budget."
    )]
    JobNonRetryableException = 23,

    [Code(
        "job.deadline-exceeded",
        "Job passed its whole-job deadline; the engine terminated it Cancelled without consuming the retry budget."
    )]
    JobDeadlineExceeded = 24,

    [Code(
        "job.attempt-aborted",
        "Worker aborted the attempt mid-flight (lease renewal at risk or a held lock lost); retried under the failure budget."
    )]
    JobAttemptAborted = 25,

    [Code("job.schedules-exhausted", "Recurring slot has no live JobSchedule yielding a next instant; row is system-paused.")]
    JobSchedulesExhausted = 30,

    // The actor (operator / system / worker) is carried separately by JobEvent.actor_code; this
    // code says WHY/HOW the control happened, not WHO.
    [Code("job.control-manual", "Operator-initiated control transition via an IJobs control verb (Cancel/Pause/Resume/Restart).")]
    JobControlManual = 40,

    [Code("job.parent-cancelled", "Job was cancelled because an ancestor job in its lineage was cancelled (recursive cascade).")]
    JobParentCancelled = 41,

    [Code(
        "job.definition-retired",
        "Job was cancelled because its definition was retired by registration; parked rows (Ready/Paused/Suspended) are cancelled set-wise, in-flight executions finish their attempt."
    )]
    JobDefinitionRetired = 42,

    [Code("job.handler-rescheduled", "Handler called ctx.RescheduleAsync; attempt finalized as Rescheduled.")]
    JobHandlerRescheduled = 50,

    [Code(
        "job.handler-suspended",
        "Handler called ctx.SleepAsync or ctx.WaitSignalAsync; attempt suspended (budget-neutral) until the sleep timer's due instant or a matching signal is raised."
    )]
    JobHandlerSuspended = 51,

    [Code(
        "job.handler-failed",
        "Handler called ctx.FailAsync; the attempt was finalized as a deliberate terminal Failed (no retry, budget untouched)."
    )]
    JobHandlerFailed = 52,

    [Code(
        "job.handler-cancelled",
        "Handler called ctx.CancelAsync; the attempt was finalized as a deliberate terminal Cancelled (no retry, budget untouched)."
    )]
    JobHandlerCancelled = 53,

    [Code("job.handler-paused", "Handler called ctx.PauseAsync; the Job was held in Paused until an external resume (budget untouched).")]
    JobHandlerPaused = 54,

    [Code("job.signal-released", "RaiseSignalAsync set a matching signal and moved a Suspended Job to Ready.")]
    JobSignalReleased = 60,

    [Code(
        "job.step-retry-scheduled",
        "An inline step failed within budget (or on replay still awaits its retry instant); the parent re-armed budget-neutral until the step's next_retry_at_utc."
    )]
    JobStepRetryScheduled = 61,

    [Code(
        "job.exclusive-key-held",
        "A claimed exclusive-key job found its key lock held at execution admission; re-armed Ready after the fixed bounce delay (budget-neutral)."
    )]
    JobExclusiveKeyHeld = 62,

    [Code(
        "job.step-interrupted",
        "An at-most-once step was re-entered on replay before its outcome was recorded (worker died mid-flight); the body is not re-run and the parent lands terminal Failed. Non-retryable; the outcome is unknown and must be reconciled externally."
    )]
    JobStepInterrupted = 63,

    // ---------- Worker / system ----------
    // worker.* events carry job_id = null; the reason lives on events, never on a job row.

    [Code("worker.clean-shutdown", "Worker process exited cleanly via SIGTERM / IHostedService.StopAsync.")]
    WorkerCleanShutdown = 100,

    [Code("worker.heartbeat-stale", "Worker heartbeat exceeded the liveness window; the sys.recovery system job flipped Status to Dead.")]
    WorkerHeartbeatStale = 101,
}
