namespace Acta;

/// <summary>The completion command math: the retry plan and the command for each completion shape.</summary>
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
    /// Failure retry math, shared by the deadline reschedule guard and the re-arm shape. MaxAttempts is the budget of
    /// a one-shot job and of a slot's firing alike: admission zeroes the slot's count, so each firing spends its own.
    /// </summary>
    internal static (int Attempted, int DelaySeconds, bool InBudget) PlanRetry(
        JobDescriptor descriptor,
        ClaimedJob job,
        ExecutionCompletion completion
    )
    {
        if (completion.Outcome != CompletionKind.Failed || !IsRetryable(completion.Reason))
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
            HasSchedule = job.ScheduleId is not null,
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
            HasSchedule = job.ScheduleId is not null,
        };

    internal static CompleteExecutionsValues BuildCommand(
        JobDescriptor descriptor,
        ClaimedJob job,
        ExecutionCompletion s,
        (int Attempted, int DelaySeconds, bool InBudget) retry
    )
    {
        var slot = job.Kind == JobKindCode.PermanentSlot;
        // Resolved once and passed on every completion shape; complete_executions stamps it onto
        // jobs_state.retention_until_utc only at a terminal landing (Succeeded/Failed/Cancelled) and ignores it on
        // re-arm / suspend / pause. Per-definition policy with the framework default as the fallback.
        // Every shape but the success ones writes no result payload.
        // A one-shot success carries its result immediately, avoiding a second command object for the common path.
        var oneShotSuccess = !slot && s.Outcome == CompletionKind.Succeeded && s.HandlerStatusCode is null;
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
            HasSchedule = job.ScheduleId is not null,
        };

        if (oneShotSuccess)
        {
            return command;
        }

        if (s.Outcome is CompletionKind.Rescheduled or CompletionKind.Suspended)
        {
            // A re-arm on a slot keeps its firing outstanding; no result payload is written (budget-neutral re-arm).
            return command with
            {
                EventCode = EventCode.JobExecutionYielded,
                RescheduleEventCode = s.Outcome == CompletionKind.Suspended ? EventCode.JobSuspended : EventCode.JobRescheduled,
                RescheduleDelaySeconds = s.RescheduleDelaySeconds,
                RescheduleResumeAtUtc = s.RescheduleResumeAtUtc,
                WaitSignalName = s.WaitSignalName,
            };
        }

        if (s.HandlerStatusCode is { } handlerStatus)
        {
            // Handler fail/cancel/pause: a deliberate terminal/hold decision; the whole Job stops. No result payload; budget
            // untouched. A slot takes the same landing through its final status, so completion settles its firing (Failed,
            // Cancelled) or keeps it outstanding under the hold (Paused, with the next run that marks it held).
            var eventCode = handlerStatus == JobStatusCode.Failed ? EventCode.JobFailed : EventCode.JobExecutionYielded;
            return slot
                ? command with
                {
                    EventCode = eventCode,
                    FinalStatus = handlerStatus,
                    JobNextRunAtUtc = handlerStatus == JobStatusCode.Paused ? DateTime.UtcNow : null,
                }
                : command with
                {
                    EventCode = eventCode,
                    HandlerStatusCode = (byte)handlerStatus,
                };
        }

        if (slot)
        {
            // A firing settles: a success and a spent budget both return the slot to Idle, where admission finds it,
            // and the envelope records which; the job lives on, so the failure is the attempt's event, never a terminal
            // one. A retryable failure within budget re-arms below like a one-shot's.
            if (s.Outcome == CompletionKind.Succeeded)
            {
                return command with
                {
                    ResultFormatId = s.ResultFormatId,
                    Result = s.Result,
                    FinalStatus = JobStatusCode.Idle,
                    FailureCount = 0,
                };
            }

            if (s.Outcome == CompletionKind.Failed && !retry.InBudget)
            {
                return command with
                {
                    EventCode = EventCode.JobExecutionFailed,
                    FinalStatus = JobStatusCode.Idle,
                    FailureCount = retry.Attempted > 0 ? retry.Attempted : NextFailureCount(job.FailureCount),
                };
            }
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
