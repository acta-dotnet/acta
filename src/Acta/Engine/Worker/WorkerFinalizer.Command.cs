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
    /// The retry plan of <paramref name="completion"/>: <paramref name="failureRetry"/> for a retryable failure, and no
    /// retry for anything else. Shared by the deadline reschedule guard and the re-arm shape.
    /// </summary>
    internal static (int Attempted, int DelaySeconds, bool InBudget) PlanRetry(
        ExecutionCompletion completion,
        (int Attempted, int DelaySeconds, bool InBudget) failureRetry
    ) => completion.Outcome == CompletionKind.Failed && IsRetryable(completion.Reason) ? failureRetry : (0, 0, false);

    /// <summary>
    /// The retry a retryable failure of <paramref name="job"/>'s attempt earns: the failure count it charges, its backoff,
    /// and whether the budget has room. MaxAttempts is the budget of a one-shot job and of a slot's firing alike:
    /// admission zeroes the slot's count, so each firing spends its own.
    /// </summary>
    internal static (int Attempted, int DelaySeconds, bool InBudget) PlanFailureRetry(JobDescriptor descriptor, ClaimedJob job)
    {
        var attempted = NextFailureCount(job.FailureCount);
        if (attempted >= descriptor.MaxAttempts)
        {
            return (attempted, 0, false);
        }

        var backoff = Backoff.Parse(descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression);
        return (attempted, BackoffSchedule.ComputeDelaySeconds(attempted, backoff), true);
    }

    /// <summary>
    /// Whether a retry <paramref name="delaySeconds"/> away would land past a Strict deadline, which ends the job Cancelled
    /// instead of re-arming it. The worker clock, as at admission: deliberate and skew-guarded, not a DB read.
    /// </summary>
    internal static bool RetryPassesDeadline(JobDescriptor descriptor, TimeSpan? untilDeadline, int delaySeconds) =>
        descriptor.DeadlineBehavior == DeadlineBehaviorCode.Strict
        && untilDeadline is { } until
        && until < TimeSpan.FromSeconds(delaySeconds);

    /// <summary>
    /// Whether a retryable failure ends the job for good, the rule <c>ctx.IsLastAttempt</c> reads: the budget has no
    /// room for the retry, or the retry would pass the deadline.
    /// </summary>
    internal static bool FailureEndsJob(
        JobDescriptor descriptor,
        (int Attempted, int DelaySeconds, bool InBudget) failureRetry,
        TimeSpan? untilDeadline
    ) => !failureRetry.InBudget || RetryPassesDeadline(descriptor, untilDeadline, failureRetry.DelaySeconds);

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
            job.WaitMs,
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

    // The handler-status shape, so the job's lane hands on and its parent's latch is raised; a slot lands through its
    // final status, as a handler's own cancel does, so completion settles its firing and a restart can revive it. The
    // retention is the retired definition's own, because a claim this worker carries no handler for has no descriptor
    // to read it from.
    private static CompleteExecutionsValues Retire(ClaimedJob job, ExecutionCompletion completion, int retentionSeconds)
    {
        var command = new CompleteExecutionsValues(
            job.JobId,
            job.ExecutionNumber,
            Succeeded: false,
            0,
            ReadOnlyMemory<byte>.Empty,
            EventCode.JobExecutionYielded,
            completion.Reason,
            completion.ReasonMessage,
            completion.DurationMs,
            job.WaitMs,
            FailureCount: null,
            retentionSeconds
        )
        {
            HasLane = job.LaneId is not null,
            HasParent = job.ParentJobId is not null,
            HasSchedule = job.ScheduleId is not null,
        };
        return job.Kind == JobKindCode.Permanent
            ? command with
            {
                FinalStatus = JobStatusCode.Cancelled,
            }
            : command with
            {
                HandlerStatusCode = (byte)JobStatusCode.Cancelled,
            };
    }

    internal static CompleteExecutionsValues BuildCommand(
        JobDescriptor descriptor,
        ClaimedJob job,
        ExecutionCompletion s,
        (int Attempted, int DelaySeconds, bool InBudget) retry
    )
    {
        var slot = job.Kind == JobKindCode.Permanent;
        // Resolved once and passed on every completion shape; complete_executions stamps it onto
        // jobs_state.retention_until_utc only at a terminal landing (Succeeded/Failed/Cancelled) and ignores it on
        // re-arm / suspend / pause. The definition row is its one source: a worker's descriptor carries the row's
        // effective retention, so no worker stamps a number of its own.
        // Every shape but the success ones writes no result payload.
        // A one-shot success carries its result and its reset failure count immediately, avoiding a second command
        // object for the common path.
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
            job.WaitMs,
            FailureCount: oneShotSuccess ? 0 : null,
            RetentionSeconds: descriptor.JobRetentionSeconds
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
                // A suspension that names no signal is a sleep: it carries the instant its timer is due.
                RescheduleEventCode =
                    s.Outcome != CompletionKind.Suspended ? EventCode.JobRescheduled
                    : s.WaitSignalName is null ? EventCode.JobSlept
                    : EventCode.JobSuspended,
                RescheduleDelaySeconds = s.RescheduleDelaySeconds,
                RescheduleResumeAtUtc = s.RescheduleResumeAtUtc,
                WaitSignalName = s.WaitSignalName,
            };
        }

        if (slot && s.Reason is EventReason.NonRetryableException or EventReason.WaitTimedOut)
        {
            // An exception no retry can change fails the firing, not the job: the slot returns to Idle for its next
            // firing as a spent budget does, with the budget untouched. Only the handler's own fail stops the job.
            return command with { EventCode = EventCode.JobExecutionFailed, FinalStatus = JobStatusCode.Idle };
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
    internal static string OutcomeTag(ExecutionCompletion completion) =>
        completion.Outcome switch
        {
            CompletionKind.Succeeded => "succeeded",
            CompletionKind.Failed => "failed",
            CompletionKind.Rescheduled => "rescheduled",
            // A suspension that names no signal is a sleep, as the event it writes says.
            CompletionKind.Suspended => completion.WaitSignalName is null ? "slept" : "suspended",
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
