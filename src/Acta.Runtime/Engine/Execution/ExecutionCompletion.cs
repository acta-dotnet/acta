using Microsoft.Extensions.Logging;

namespace Acta.Runtime;

/// <summary>
/// Writes how an attempt settled: the one-shot retry budget and its Strict deadline guard, the
/// <c>complete_execution</c> command for each completion shape (including the recurring-completion outcome math),
/// the write itself, and the wakes and follow-ups its final state calls for.
/// </summary>
internal sealed class ExecutionCompletion(
    Execution execution,
    CancelJobHandler cancelJob,
    WorkerWakeupPublisher wakeupPublisher,
    ILogger log,
    JobMetrics? metrics
)
{
    public async Task<RunOnceStatus> CompleteAsync(
        JobDescriptor descriptor,
        ClaimedJob job,
        RuntimeJobContext jobContext,
        int workerId,
        RecurringFirePlan? fire,
        AttemptSettlement settlement,
        CancellationToken ct
    )
    {
        var retry = PlanRetry(descriptor, job, fire, settlement);

        // Deadline reschedule guard (Strict): if the next retry would land after the deadline,
        // terminate Cancelled+JobDeadlineExceeded via the handler status instead of re-arming.
        // Budget is left untouched; the handler-status shape handles completion. Same worker-clock
        // comparison as the admission check: deliberate and skew-guarded, not a DB read.
        if (
            retry.InBudget
            && descriptor.DeadlineBehavior == DeadlineBehaviorCode.Strict
            && jobContext.TimeUntilDeadline is { } untilDeadline
            && untilDeadline < TimeSpan.FromSeconds(retry.DelaySeconds)
        )
        {
            settlement = settlement with
            {
                Outcome = CompletionKind.Cancelled,
                Reason = EventReasonCode.JobDeadlineExceeded,
                ReasonMessage = "Next retry would exceed the job deadline.",
                HandlerStatusCode = JobStatusCode.Cancelled,
            };
        }

        var command = BuildCommand(descriptor, job, workerId, fire, settlement, retry);

        // ct is the worker's host token, never jobContext.CancellationToken; CompletionWrite says why.
        var (complete, retried) = await CompletionWrite.RetryAsync(
            token => execution.CompleteExecutionAsync(command, token),
            log,
            job.JobId,
            ct,
            metrics
        );

        if (complete.Action != CompleteExecutionAction.Completed)
        {
            // An external cancel, cascade, or stolen lease moved the row out of execution while the
            // handler ran, so the compare-and-swap completed no work. A terminal row is always a
            // clean skip because a fast handler can finish before heartbeat cancellation reaches the
            // attempt token, and so is a lost lease after a retried write: the first try committed
            // and cleared the lease before its answer was lost, so the row already holds this
            // outcome. Losing ownership on a first try with an uncancelled token is a genuine anomaly.
            return
                complete.Action == CompleteExecutionAction.AlreadyTerminal
                || retried
                || jobContext.CancellationToken.IsCancellationRequested
                ? RunOnceStatus.NothingClaimed
                : throw new InvalidOperationException($"CompleteExecution for job {job.JobId} returned {complete.Action}.");
        }

        await WakeAsync(complete, job, jobContext, ct);

        // Handler cancel: cancel the descendant subtree as follow-up transactions (the completion
        // above already raised this job's own latch in-routine); maintenance is the crash backstop.
        if (settlement.HandlerStatusCode == JobStatusCode.Cancelled)
        {
            await cancelJob.CancelDescendantsAsync(job.JobId, CancelJobHandler.ParentCancelled, ct);
        }

        // One measurement per completed execution: the single emit point both the poll loop and the
        // single-shot path funnel through. reason_code is the failure cause's kebab (null on success).
        metrics?.RecordExecution(
            jobContext.JobNamespace,
            descriptor.JobName,
            OutcomeTag(settlement.Outcome),
            settlement.Reason?.Code,
            settlement.DurationMs
        );

        return settlement.Outcome switch
        {
            // A failed one-shot that re-armed for another attempt: the row is Ready, not Failed.
            CompletionKind.Failed when retry.InBudget => RunOnceStatus.Rearmed,
            CompletionKind.Succeeded or CompletionKind.Cancelled => RunOnceStatus.Completed,
            CompletionKind.Rescheduled or CompletionKind.Suspended or CompletionKind.Paused => RunOnceStatus.Rearmed,
            _ => RunOnceStatus.Failed,
        };
    }

    /// <summary>
    /// One-shot failure retry math, shared by the deadline reschedule guard and the re-arm shape. One-shot only:
    /// MaxAttempts is the one-off retry budget, and a recurring slot rolls over instead (its null DeadlineAtUtc
    /// keeps the deadline guard off it too).
    /// </summary>
    private static (int Attempted, int DelaySeconds, bool InBudget) PlanRetry(
        JobDescriptor descriptor,
        ClaimedJob job,
        RecurringFirePlan? fire,
        AttemptSettlement settlement
    )
    {
        if (fire is not null || settlement.Outcome != CompletionKind.Failed || !IsRetryable(settlement.Reason))
        {
            return (0, 0, false);
        }

        var attempted = job.FailureCount + 1;
        if (attempted >= descriptor.MaxAttempts)
        {
            return (attempted, 0, false);
        }

        var backoff = Backoff.Parse(descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression);
        return (attempted, BackoffSchedule.ComputeDelaySeconds(attempted, backoff), true);
    }

    private static CompleteExecutionCommand BuildCommand(
        JobDescriptor descriptor,
        ClaimedJob job,
        int workerId,
        RecurringFirePlan? fire,
        AttemptSettlement s,
        (int Attempted, int DelaySeconds, bool InBudget) retry
    )
    {
        // Resolved once and passed on every completion shape; complete_execution stamps it onto
        // runtimes.retention_until_utc only at a terminal landing (Succeeded/Failed/Cancelled) and ignores it on
        // re-arm / suspend / pause. Per-definition policy with the framework default as the fallback.
        // Every shape but the recurring and the success one writes no result payload.
        var payloadless = new CompleteExecutionCommand(
            job.JobId,
            workerId,
            job.ExecutionNumber,
            s.Outcome,
            0,
            ReadOnlyMemory<byte>.Empty,
            s.Reason,
            s.ReasonMessage,
            s.DurationMs
        )
        {
            RetentionSeconds = descriptor.JobRetentionSeconds ?? JobDefinitionRegistration.DefaultJobRetentionSeconds,
        };

        if (fire is { NothingDue: true })
        {
            // The recurring shape applies the advances, which clear the elapsed pauses, and reads the slot's
            // next run from the schedules; the re-arm status records a re-arm rather than a run, so the
            // failure count, the results, and any open incident stand as they were.
            return payloadless with
            {
                EventReasonCode = null,
                ScheduleAdvances = fire.Advances,
                FinalStatus = JobStatusCode.Ready,
                JobNextRunAtUtc = fire.SlotMinNextRunAtUtc,
                RescheduleStatusCode = (byte)ExecutionStatusCode.Rescheduled,
                // A Ready row must carry a next run before the recurring block sets the real one.
                RescheduleResumeAtUtc = fire.SlotMinNextRunAtUtc ?? DateTime.UtcNow,
            };
        }

        if (s.Outcome is CompletionKind.Rescheduled or CompletionKind.Suspended)
        {
            // Re-arm takes the non-recurring shape even on a recurring slot so its schedule
            // cursors are never advanced; no result payload is written (budget-neutral re-arm).
            return payloadless with
            {
                RescheduleStatusCode = (byte)(
                    s.Outcome == CompletionKind.Suspended ? ExecutionStatusCode.Suspended : ExecutionStatusCode.Rescheduled
                ),
                RescheduleDelaySeconds = s.RescheduleDelaySeconds,
                RescheduleResumeAtUtc = s.RescheduleResumeAtUtc,
                WaitSignalName = s.WaitSignalName,
            };
        }

        if (s.HandlerStatusCode is { } handlerStatus)
        {
            // Handler fail/cancel/pause: a deliberate terminal/hold decision. Takes the non-recurring
            // shape even on a recurring slot (like the re-arm shape) so its schedule cursors are never
            // advanced; the whole Job stops. No result payload; budget untouched.
            return payloadless with { HandlerStatusCode = (byte)handlerStatus };
        }

        if (fire is not null)
        {
            var (finalStatus, failureCount, reasonCode, reasonMessage) = ComputeRecurringOutcome(
                s.Outcome,
                job,
                fire,
                s.Reason,
                s.ReasonMessage
            );
            return payloadless with
            {
                ResultFormatId = s.ResultFormatId,
                Result = s.Result,
                EventReasonCode = reasonCode,
                ReasonMessage = reasonMessage,
                ScheduleAdvances = fire.Advances,
                FinalStatus = finalStatus,
                JobNextRunAtUtc = fire.SlotMinNextRunAtUtc,
                FailureCount = failureCount,
                RecurringResultCap = descriptor.RecurringResultCap,
            };
        }

        if (s.Outcome == CompletionKind.Failed && IsRetryable(s.Reason))
        {
            // One-shot failure (unhandled exception / execution timeout / aborted attempt): honor the retry budget.
            // In budget: re-arm to Ready with a backoff delay and persist the bumped failure_count.
            // Exhausted: terminal Failed, keeping the final count.
            // The Strict deadline guard may have already converted this to Cancelled with a handler status,
            // routing to the handler-status shape before this one is reached.
            return retry.InBudget
                ? payloadless with
                {
                    RescheduleStatusCode = (byte)ExecutionStatusCode.Rescheduled,
                    RescheduleDelaySeconds = retry.DelaySeconds,
                    FailureCount = retry.Attempted,
                }
                : payloadless with
                {
                    FailureCount = retry.Attempted,
                };
        }

        return payloadless with
        {
            ResultFormatId = s.ResultFormatId,
            Result = s.Result,
        };
    }

    /// <summary>
    /// Recurring completion outcome from the attempt and the planned slot MIN. A recurring slot re-arms Ready on any
    /// failure (MaxAttempts is the one-off budget only); exhausted schedules pause; a success resets the failure
    /// counter and a failure bumps it. The Ready rollover keeps the attempt's reason like every other shape: the
    /// alertable set admits a Ready event only for reasons 20/21/22 (GetAlertableEvents.sql), so a nightly job that
    /// throws every night still alerts and explains. An aborted attempt (25) rolls over Ready without alerting.
    /// </summary>
    internal static (
        JobStatusCode FinalStatus,
        int FailureCount,
        EventReasonCode? ReasonCode,
        string? ReasonMessage
    ) ComputeRecurringOutcome(
        CompletionKind outcome,
        ClaimedJob job,
        RecurringFirePlan fire,
        EventReasonCode? failureReason,
        string? failureMessage
    )
    {
        var failureCount = outcome == CompletionKind.Succeeded ? 0 : job.FailureCount + 1;

        if (fire.SlotMinNextRunAtUtc is null)
        {
            return (JobStatusCode.Paused, failureCount, EventReasonCode.JobSchedulesExhausted, null);
        }

        return (JobStatusCode.Ready, failureCount, failureReason, failureMessage);
    }

    /// <summary>
    /// Publishes on the FINAL state the routine reports, never the category the caller guessed: a signal that arrived
    /// Set while the handler ran lands the job Ready inside complete_execution, which no other publish site sees. A
    /// final Ready wakes the namespace's claim loops (due now is WorkAvailable, a run time ahead of db_now is
    /// HorizonChanged so sleeping loops re-read their horizon); a final terminal status wakes the job's completion
    /// channel for a colocated RunAndWaitAsync caller.
    /// </summary>
    private async Task WakeAsync(CompleteExecutionResponse complete, ClaimedJob job, RuntimeJobContext jobContext, CancellationToken ct)
    {
        if (complete.FinalStatusCode == (byte)JobStatusCode.Ready)
        {
            var wakeReason =
                complete.FinalNextRunAtUtc is { } nextRun && nextRun > complete.DbNowUtc
                    ? WorkerWakeupReason.HorizonChanged
                    : WorkerWakeupReason.WorkAvailable;
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(jobContext.JobNamespace), wakeReason, ct);
        }
        else if (complete.FinalStatusCode is { } finalStatus && ((JobStatusCode)finalStatus).IsTerminal)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(job.JobId), WorkerWakeupReason.JobFinished, ct);
        }

        // The settle handed a lane to its next member inside complete_execution; lanes are namespace-scoped,
        // so the member is claimable in this namespace.
        if (complete.LanePromoted)
        {
            await wakeupPublisher.WakeAsync(
                WorkerWakeupChannel.WorkerNamespace(jobContext.JobNamespace),
                WorkerWakeupReason.WorkAvailable,
                ct
            );
        }

        // A terminal child's raise flipped its Suspended parent to Ready inside complete_execution;
        // the routine knows only the parent's numeric namespace id, so every worker namespace hears it.
        await JobControl.WakeWorkersIfAsync(wakeupPublisher, complete.ParentReleased, ct);
    }

    /// <summary>
    /// Stable low-cardinality tag value for the executions/duration metrics. Kept off ToString() so
    /// an enum rename can't silently rename an operator-facing metric dimension.
    /// </summary>
    private static string OutcomeTag(CompletionKind outcome) =>
        outcome switch
        {
            CompletionKind.Succeeded => "succeeded",
            CompletionKind.Failed => "failed",
            CompletionKind.Rescheduled => "rescheduled",
            CompletionKind.Suspended => "suspended",
            CompletionKind.Cancelled => "cancelled",
            CompletionKind.Paused => "paused",
            _ => "unknown",
        };

    /// <summary>
    /// Failures eligible for the one-shot retry budget: an unhandled exception, an execution
    /// timeout, or an attempt aborted by lease/lock pressure. A deliberate ctx.FailAsync
    /// (HandlerFailed) takes the handler-status path and never retries; a null reason falls through
    /// to a terminal completion.
    /// </summary>
    private static bool IsRetryable(EventReasonCode? reason) =>
        reason is EventReasonCode.JobUnhandledException or EventReasonCode.JobExecutionTimeout or EventReasonCode.JobAttemptAborted;
}
