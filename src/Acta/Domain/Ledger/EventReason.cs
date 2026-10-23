namespace Acta;

/// <summary>
/// The <c>details.reason</c> vocabulary: why a job event happened, as a kebab string inside the event's JSON details.
/// A reason the event code already implies (an operator's control verb, a worker's clean shutdown) is not written.
/// </summary>
internal static class EventReason
{
    /// <summary>The handler threw an exception it did not raise through ctx.FailAsync.</summary>
    public const string UnhandledException = "unhandled-exception";

    /// <summary>The worker's lease expired and crash recovery reclaimed the job.</summary>
    public const string LeaseExpired = "lease-expired";

    /// <summary>The attempt exceeded the definition's execution timeout.</summary>
    public const string ExecutionTimeout = "execution-timeout";

    /// <summary>
    /// The handler threw an exception the runtime classifies as non-retryable (NotImplementedException,
    /// NotSupportedException, or an uncaught StepExhaustedException).
    /// </summary>
    public const string NonRetryableException = "non-retryable-exception";

    /// <summary>The job passed its whole-job deadline and ended Cancelled without consuming the retry budget.</summary>
    public const string DeadlineExceeded = "deadline-exceeded";

    /// <summary>
    /// The worker aborted the attempt mid-flight (lease renewal at risk, a held lock lost, or a step's ownership
    /// unconfirmed); retried under the failure budget, and no alert of its own.
    /// </summary>
    public const string AttemptAborted = "attempt-aborted";

    /// <summary>A recurring slot has no live schedule yielding a next instant, so it paused.</summary>
    public const string SchedulesExhausted = "schedules-exhausted";

    /// <summary>An ancestor job in the lineage was cancelled, and the cancel cascaded.</summary>
    public const string ParentCancelled = "parent-cancelled";

    /// <summary>An operator retired the job's definition, so the job does not run again.</summary>
    public const string DefinitionRetired = "definition-retired";

    /// <summary>The handler called ctx.RescheduleAsync.</summary>
    public const string HandlerRescheduled = "handler-rescheduled";

    /// <summary>The handler called ctx.SleepAsync or a durable wait.</summary>
    public const string HandlerSuspended = "handler-suspended";

    /// <summary>The handler called ctx.FailAsync: a deliberate terminal failure, budget untouched.</summary>
    public const string HandlerFailed = "handler-failed";

    /// <summary>The handler called ctx.CancelAsync.</summary>
    public const string HandlerCancelled = "handler-cancelled";

    /// <summary>The handler called ctx.PauseAsync.</summary>
    public const string HandlerPaused = "handler-paused";

    /// <summary>A raised signal or a child's outcome released a Suspended job to Ready.</summary>
    public const string SignalReleased = "signal-released";

    /// <summary>An inline step failed within its budget, so the job re-armed until the step's retry instant.</summary>
    public const string StepRetryScheduled = "step-retry-scheduled";

    /// <summary>A concurrency-key job found its key held at admission and re-armed after the bounce delay.</summary>
    public const string ConcurrencyKeyHeld = "concurrency-key-held";

    /// <summary>
    /// An at-most-once step was re-entered on replay before its outcome was recorded; the job failed, and the
    /// outcome must be reconciled externally.
    /// </summary>
    public const string StepInterrupted = "step-interrupted";

    /// <summary>The handler succeeded, but its result exceeded MaxInlinePayloadBytes and was dropped.</summary>
    public const string ResultOversized = "result-oversized";

    /// <summary>A durable wait passed its expiration before it was satisfied.</summary>
    public const string WaitTimedOut = "wait-timed-out";

    /// <summary>The definition's rate limit was reached at admission; the job re-armed at its reserved turn.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>The job's lane had no live head, so crash recovery released its lowest Blocked member.</summary>
    public const string LaneRepaired = "lane-repaired";
}
