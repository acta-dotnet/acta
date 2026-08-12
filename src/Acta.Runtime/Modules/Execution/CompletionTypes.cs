namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// A computed cursor advance for one schedule, applied by <c>complete_execution</c> on a recurring fire.
/// A null <c>NextRunAtUtc</c> clears the schedule's cursor (no further occurrences).
/// </summary>
internal sealed record ScheduleAdvance(long ScheduleId, DateTime? NextRunAtUtc);

/// <summary>
/// Per-attempt completion request. <c>Outcome</c> chooses the terminal status; result bytes
/// are written to <c>results</c> only when <c>Outcome</c> is <c>Succeeded</c> and
/// <c>ResultFormatId</c> is non-zero (the descriptor declared an output type).
/// </summary>
/// <remarks>
/// The recurring fields are populated only for a recurring slot fire. A non-empty
/// <c>ScheduleAdvances</c> selects the recurring branch in <c>complete_execution</c>:
/// <c>FinalStatus</c> overrides the <c>Outcome</c>-derived terminal status (the slot re-arms to
/// <c>Ready</c>, fails, or pauses), <c>JobNextRunAtUtc</c> is the slot MIN, <c>FailureCount</c> is
/// the computed budget counter, and <c>RecurringResultCap</c> trims <c>results</c> to the newest N.
/// Empty advances run the non-recurring flow.
/// </remarks>
internal sealed record CompleteExecutionRequest(
    long JobId,
    int WorkerId,
    int ExpectedExecutionNumber,
    ExecutionOutcome Outcome,
    byte ResultFormatId,
    ReadOnlyMemory<byte> Result,
    JobEventReasonCode? JobEventReasonCode = null,
    string? ReasonMessage = null,
    int? DurationMs = null,
    IReadOnlyList<ScheduleAdvance>? ScheduleAdvances = null,
    JobStatusCode? FinalStatus = null,
    DateTime? JobNextRunAtUtc = null,
    short? FailureCount = null,
    int RecurringResultCap = 0
)
{
    /// <summary>
    /// Non-null selects the re-arm branch: the execution-row status (<c>8</c> Rescheduled / <c>9</c>
    /// Suspended). The Job flips to <c>Ready</c> instead of a terminal status. Set on the non-recurring
    /// path only; re-arm never advances recurring schedule cursors.
    /// </summary>
    public byte? RescheduleStatusCode { get; init; }

    /// <summary>Relative re-arm delay; the routine computes <c>db_now + delay</c>. Null when an absolute
    /// instant is supplied.</summary>
    public int? RescheduleDelaySeconds { get; init; }

    /// <summary>Absolute re-arm instant; used verbatim as <c>next_run_at_utc</c>. Null when a relative
    /// delay is supplied.</summary>
    public DateTime? RescheduleResumeAtUtc { get; init; }

    /// <summary>
    /// Signal-suspend only: the awaited signal name. Non-null alongside <c>RescheduleStatusCode</c> = 9
    /// (Suspended) selects the signal branch: the routine locks the slot and lands the Job in real
    /// <c>Suspended</c> (no <c>next_run_at_utc</c>), or <c>Ready</c> when the slot already arrived
    /// <c>Set</c>. Null for sleep-suspend and ordinary completion.
    /// </summary>
    public string? WaitSignalName { get; init; }

    /// <summary>
    /// Handler-control only: the deliberate target Status for <c>ctx.FailAsync</c> (200 Failed),
    /// <c>ctx.CancelAsync</c> (220 Cancelled), or <c>ctx.PauseAsync</c> (10 Paused). Non-null overrides
    /// the <c>Outcome</c>-derived terminal status, sets <c>next_run_at_utc</c> to NULL, leaves the
    /// failure budget untouched, and emits the matching lifecycle event. Set on the non-recurring path
    /// only; a handler termination never advances recurring schedule cursors.
    /// </summary>
    public byte? HandlerStatusCode { get; init; }

    /// <summary>
    /// Seconds added to <c>db_now</c> to stamp <c>runtimes.retention_until_utc</c> when this completion lands
    /// the Job in a terminal status (<c>100</c> Succeeded / <c>200</c> Failed / <c>220</c> Cancelled). Null
    /// leaves the column untouched; re-arm, suspend, and pause completions never stamp retention.
    /// <c>JobRunner</c> resolves it from the definition's <c>JobRetentionSeconds</c> or the framework default.
    /// </summary>
    public int? RetentionSeconds { get; init; }
}

internal enum CompleteExecutionAction : byte
{
    Completed = 1,
    NotOwner = 2,
    AlreadyTerminal = 3,
}

/// <summary>
/// Completion outcome plus the job's resulting runtime state, read in the same round trip so the
/// caller can react to where the row landed. Publishes a wakeup when the final status is <c>Ready</c>
/// (immediate retry, recurring roll-over, or a signal that arrived <c>Set</c> while the handler ran),
/// the one Ready transition no other publish site can see.
/// </summary>
/// <param name="Action">Whether this call completed the attempt, lost ownership (NotOwner), or found the job already terminal.</param>
/// <param name="FinalStatusCode">The job's <c>status_code</c> after this call: on <c>Completed</c> the status this completion produced; on NotOwner/AlreadyTerminal the row's current status (null when the row no longer exists).</param>
/// <param name="FinalNextRunAtUtc">The job's <c>next_run_at_utc</c> after this call, on the same produced-vs-current basis as <paramref name="FinalStatusCode"/>.</param>
/// <param name="DbNowUtc">The routine's clock reading, for due-now comparison against <paramref name="FinalNextRunAtUtc"/> with no host-clock assumption.</param>
/// <param name="ParentReleased">True when this terminal landing's child-done raise flipped a Suspended parent to Ready; the caller wakes all worker namespaces (the parent may live in another namespace).</param>
internal sealed record CompleteExecutionResult(
    CompleteExecutionAction Action,
    byte? FinalStatusCode,
    DateTime? FinalNextRunAtUtc,
    DateTime DbNowUtc,
    bool ParentReleased
);

/// <summary>One <c>complete_executions_batch</c> outcome row: the request ordinal and whether it was finalized here.</summary>
internal readonly record struct BatchOutcomeRow(int Ordinal, bool Finalized);
