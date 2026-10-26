namespace Acta;

/// <summary>The completion command math: the retry plan, the command for each completion shape, and the recurring outcome.</summary>
internal sealed partial class WorkerFinalizer
{
    /// <summary>
    /// The ceiling <c>jobs_state.failure_count</c> saturates at: a count there stops climbing, which the dashboard
    /// shows as <c>32,767+</c>. Registration caps MaxAttempts at it, so a saturated count still spends any budget.
    /// </summary>
    internal const int FailureCountCeiling = short.MaxValue;

    /// <summary>The failure count after one more charged failure, saturating at <see cref="FailureCountCeiling"/>.</summary>
    internal static int NextFailureCount(int failureCount) => failureCount >= FailureCountCeiling ? FailureCountCeiling : failureCount + 1;

    /// <summary>
    /// One-shot failure retry math, shared by the deadline reschedule guard and the re-arm shape. One-shot only:
    /// MaxAttempts is the one-off retry budget, and a recurring slot rolls over instead (its null DeadlineAtUtc
    /// keeps the deadline guard off it too).
    /// </summary>
    internal static (int Attempted, int DelaySeconds, bool InBudget) PlanRetry(
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

        var attempted = NextFailureCount(job.FailureCount);
        if (attempted >= descriptor.MaxAttempts)
        {
            return (attempted, 0, false);
        }

        var backoff = Backoff.Parse(descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression);
        return (attempted, BackoffSchedule.ComputeDelaySeconds(attempted, backoff), true);
    }

    // No reason: none describes a worker declining a claim, and a borrowed one would misreport it. The message
    // tells the story, and the yielded event it writes stays out of the Failures level and the alertable set, where a
    // bounce belongs: nothing failed.
    private CompleteExecutionsValues HandBack(ClaimedJob job, ExecutionCompletion completion) =>
        new(
            job.JobId,
            job.ExecutionNumber,
            Succeeded: false,
            0,
            ReadOnlyMemory<byte>.Empty,
            EventCode.JobExecutionYielded,
            Reason: null,
            completion.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
            DurationMs: 0,
            FailureCount: null,
            RetentionSeconds: null
        )
        {
            RescheduleEventCode = EventCode.JobRescheduled,
            RescheduleDelaySeconds = _handBackDelaySeconds,
            HasLane = job.LaneId is not null,
            HasParent = job.ParentJobId is not null,
        };

    // The handler-status shape, so the job's lane hands on and its parent's latch is raised. The retention is the
    // retired definition's own, because a claim this worker carries no handler for has no descriptor to read it from.
    private static CompleteExecutionsValues Retire(ClaimedJob job, ExecutionCompletion completion, int retentionSeconds) =>
        new(
            job.JobId,
            job.ExecutionNumber,
            Succeeded: false,
            0,
            ReadOnlyMemory<byte>.Empty,
            EventCode.JobExecutionYielded,
            completion.Reason,
            completion.ReasonMessage,
            completion.DurationMs,
            FailureCount: null,
            retentionSeconds
        )
        {
            HandlerStatusCode = (byte)JobStatusCode.Cancelled,
            HasLane = job.LaneId is not null,
            HasParent = job.ParentJobId is not null,
        };

    internal static CompleteExecutionsValues BuildCommand(
        JobDescriptor descriptor,
        ClaimedJob job,
        RecurringFirePlan? fire,
        ExecutionCompletion s,
        (int Attempted, int DelaySeconds, bool InBudget) retry
    )
    {
        // Resolved once and passed on every completion shape; complete_executions stamps it onto
        // jobs_state.retention_until_utc only at a terminal landing (Succeeded/Failed/Cancelled) and ignores it on
        // re-arm / suspend / pause. Per-definition policy with the framework default as the fallback.
        // Every shape but the recurring and the success one writes no result payload.
        // A one-shot success carries its result immediately, avoiding a second command object for the common path.
        var oneShotSuccess = fire is null && s.Outcome == CompletionKind.Succeeded && s.HandlerStatusCode is null;
        var command = new CompleteExecutionsValues(
            job.JobId,
            job.ExecutionNumber,
            s.Outcome == CompletionKind.Succeeded,
            oneShotSuccess ? s.ResultFormatId : (byte)0,
            oneShotSuccess ? s.Result : ReadOnlyMemory<byte>.Empty,
            s.Outcome == CompletionKind.Succeeded ? EventCode.JobExecutionSucceeded : EventCode.JobFailed,
            s.Reason,
            s.ReasonMessage,
            s.DurationMs,
            FailureCount: null,
            RetentionSeconds: descriptor.JobRetentionSeconds ?? JobDefinitionRegistration.DefaultJobRetentionSeconds
        )
        {
            HasLane = job.LaneId is not null,
            HasParent = job.ParentJobId is not null,
        };

        if (oneShotSuccess)
        {
            return command;
        }

        if (fire is { NothingDue: true })
        {
            // The recurring shape applies the advances, which clear the elapsed pauses, and reads the slot's
            // next run from the schedules; the re-arm records a yield rather than a run, so the failure count and
            // any open incident stand as they were.
            return command with
            {
                EventCode = EventCode.JobExecutionYielded,
                Reason = null,
                ScheduleAdvances = fire.Advances,
                FinalStatus = JobStatusCode.Ready,
                JobNextRunAtUtc = fire.SlotMinNextRunAtUtc,
                RescheduleEventCode = EventCode.JobRescheduled,
                // A Ready row must carry a next run before the recurring block sets the real one.
                RescheduleResumeAtUtc = fire.SlotMinNextRunAtUtc ?? DateTime.UtcNow,
            };
        }

        if (s.Outcome is CompletionKind.Rescheduled or CompletionKind.Suspended)
        {
            // Re-arm takes the non-recurring shape even on a recurring slot so its schedule
            // cursors are never advanced; no result payload is written (budget-neutral re-arm).
            return command with
            {
                EventCode = EventCode.JobExecutionYielded,
                RescheduleEventCode = s.Outcome == CompletionKind.Suspended ? EventCode.JobSuspended : EventCode.JobRescheduled,
                RescheduleDelaySeconds = s.RescheduleDelaySeconds,
                RescheduleResumeAtUtc = s.RescheduleResumeAtUtc,
                WaitSignalName = s.WaitSignalName,
            };
        }

        // A non-retryable exception ends a one-off job, but a fire's is one more failed occurrence: it rolls over below,
        // since a library throwing NotSupportedException on one night's data must not stop the schedule for good.
        if (s.HandlerStatusCode is { } handlerStatus && !(fire is not null && s.Reason == EventReason.NonRetryableException))
        {
            // Handler fail/cancel/pause: a deliberate terminal/hold decision. Takes the non-recurring
            // shape even on a recurring slot (like the re-arm shape) so its schedule cursors are never
            // advanced; the whole Job stops. No result payload; budget untouched.
            return command with
            {
                EventCode = handlerStatus == JobStatusCode.Failed ? EventCode.JobFailed : EventCode.JobExecutionYielded,
                HandlerStatusCode = (byte)handlerStatus,
            };
        }

        if (fire is not null)
        {
            var (finalStatus, failureCount, reason, reasonMessage) = ComputeRecurringOutcome(
                s.Outcome,
                job,
                fire,
                s.Reason,
                s.ReasonMessage
            );
            return command with
            {
                ResultFormatId = s.ResultFormatId,
                Result = s.Result,
                // A fire never lands Failed: a failed occurrence rolls the slot over or pauses it.
                EventCode = s.Outcome == CompletionKind.Succeeded ? EventCode.JobExecutionSucceeded : EventCode.JobExecutionFailed,
                Reason = reason,
                EventMessage = reasonMessage,
                ScheduleAdvances = fire.Advances,
                FinalStatus = finalStatus,
                JobNextRunAtUtc = fire.SlotMinNextRunAtUtc,
                FailureCount = failureCount,
            };
        }

        if (s.Outcome == CompletionKind.Failed && IsRetryable(s.Reason))
        {
            // One-shot failure (unhandled exception / execution timeout / aborted attempt): honor the retry budget.
            // In budget: re-arm to Ready with a backoff delay, record the charged attempt as failed, and persist the
            // bumped failure_count.
            // Exhausted: terminal Failed, keeping the final count.
            // The Strict deadline guard may have already converted this to Cancelled with a handler status,
            // routing to the handler-status shape before this one is reached.
            return retry.InBudget
                ? command with
                {
                    EventCode = EventCode.JobExecutionFailed,
                    RescheduleEventCode = EventCode.JobRescheduled,
                    RescheduleDelaySeconds = retry.DelaySeconds,
                    FailureCount = retry.Attempted,
                }
                : command with
                {
                    FailureCount = retry.Attempted,
                };
        }

        return command with
        {
            ResultFormatId = s.ResultFormatId,
            Result = s.Result,
        };
    }

    /// <summary>
    /// Recurring completion outcome from the attempt and the planned slot MIN. A recurring slot re-arms Ready on any
    /// failure (MaxAttempts is the one-off budget only); exhausted schedules pause; a success resets the failure
    /// counter and a failure bumps it, saturating. The Ready rollover keeps the attempt's reason like every other
    /// shape, so a nightly job that throws every night still alerts and explains.
    /// </summary>
    internal static (JobStatusCode FinalStatus, int FailureCount, string? Reason, string? ReasonMessage) ComputeRecurringOutcome(
        CompletionKind outcome,
        ClaimedJob job,
        RecurringFirePlan fire,
        string? failureReason,
        string? failureMessage
    )
    {
        var failureCount = outcome == CompletionKind.Succeeded ? 0 : NextFailureCount(job.FailureCount);

        if (fire.SlotMinNextRunAtUtc is null)
        {
            return (JobStatusCode.Paused, failureCount, EventReason.SchedulesExhausted, null);
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
    /// (handler-failed) takes the handler-status path and never retries; a null reason falls through
    /// to a terminal completion.
    /// </summary>
    private static bool IsRetryable(string? reason) =>
        reason is EventReason.UnhandledException or EventReason.ExecutionTimeout or EventReason.AttemptAborted;
}
