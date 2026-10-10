namespace Acta;

/// <summary>The completion command math: the retry plan, the command for each completion shape, and the recurring outcome.</summary>
internal sealed partial class JobFinalizer
{
    /// <summary>
    /// One-shot failure retry math, shared by the deadline reschedule guard and the re-arm shape. One-shot only:
    /// MaxAttempts is the one-off retry budget, and a recurring slot rolls over instead (its null DeadlineAtUtc
    /// keeps the deadline guard off it too).
    /// </summary>
    private static (int Attempted, int DelaySeconds, bool InBudget) PlanRetry(
        JobDescriptor descriptor,
        ClaimedJob job,
        RecurringFirePlan? fire,
        ExecutionCompletion completion
    )
    {
        if (fire is not null || completion.Outcome != CompletionKind.Failed || !IsRetryable(completion.Reason))
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

    // Unclassified rather than a borrowed code: no catalog reason describes a worker declining a
    // claim, and the two candidates would both misreport it - JobAttemptAborted is published as a
    // mid-flight abort retried under the failure budget, JobDefinitionRetired as a catalog
    // retirement that cancels. Unclassified is the writer saying the story is in the message, and
    // it stays out of both the alertable set and the Failures audit level, which a bounce belongs
    // outside of: nothing failed.
    private CompleteExecutionCommand HandBack(ClaimedJob job, int workerId, ExecutionCompletion completion) =>
        new(
            job.JobId,
            workerId,
            job.ExecutionNumber,
            CompletionKind.Rescheduled,
            0,
            ReadOnlyMemory<byte>.Empty,
            EventReasonCode.Unclassified,
            completion.ReasonMessage.Truncate(ActaTextLimits.ReasonMessage),
            DurationMs: 0
        )
        {
            RescheduleStatusCode = (byte)ExecutionStatusCode.Rescheduled,
            RescheduleDelaySeconds = _handBackDelaySeconds,
        };

    private static CompleteExecutionCommand BuildCommand(
        JobDescriptor descriptor,
        ClaimedJob job,
        int workerId,
        RecurringFirePlan? fire,
        ExecutionCompletion s,
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
