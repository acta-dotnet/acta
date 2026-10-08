namespace Acta.Runtime;

/// <summary>
/// One attempt's completion. <c>Outcome</c> picks the terminal status; result bytes are written only on <c>Succeeded</c>
/// with a non-zero <c>ResultFormatId</c> (the descriptor declared an output type). A non-empty <c>ScheduleAdvances</c>
/// (a recurring slot fire) selects the recurring branch: <c>FinalStatus</c> overrides the outcome's status (the slot
/// re-arms to Ready, fails, or pauses), <c>JobNextRunAtUtc</c> is the slot MIN, <c>FailureCount</c> the computed
/// budget counter, and <c>RecurringResultCap</c> trims the results to the newest N.
/// </summary>
internal sealed record CompleteExecutionCommand(
    long JobId,
    int WorkerId,
    int ExpectedExecutionNumber,
    CompletionKind Outcome,
    byte ResultFormatId,
    ReadOnlyMemory<byte> Result,
    EventReasonCode? EventReasonCode = null,
    string? ReasonMessage = null,
    int? DurationMs = null,
    IReadOnlyList<ScheduleAdvance>? ScheduleAdvances = null,
    JobStatusCode? FinalStatus = null,
    DateTime? JobNextRunAtUtc = null,
    int? FailureCount = null,
    int RecurringResultCap = 0
)
{
    /// <summary>
    /// Non-null selects the re-arm branch: the execution-row status (<c>8</c> Rescheduled / <c>9</c>
    /// Suspended). The Job flips to <c>Ready</c> instead of a terminal status. Set on the non-recurring
    /// path, where a re-arm never advances recurring schedule cursors, and on a recurring fire that ran
    /// nothing because its elapsed timed pauses left no occurrence due: there the advances apply, and the
    /// status only records the attempt as a re-arm rather than a run.
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
    /// <c>Suspended</c> carrying the slot's expiration as <c>next_run_at_utc</c> (NULL on an unbounded
    /// wait), or <c>Ready</c> when the slot already arrived <c>Set</c>. Null for sleep-suspend and
    /// ordinary completion.
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
    /// <c>ExecutionCompletion</c> resolves it from the definition's <c>JobRetentionSeconds</c> or the framework default.
    /// </summary>
    public int? RetentionSeconds { get; init; }
}
