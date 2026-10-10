using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// Writes each execution's completion: the one-shot retry budget and its Strict deadline guard, the
/// <c>complete_execution</c> command for each completion shape (including the recurring-completion outcome math), the
/// write, repeated until it lands, and the wakes and follow-ups its final state calls for. Its loops take what the
/// executors completed and write the plain completions together in one <c>complete_executions</c> call, so an executor
/// is free as soon as its completion is built, while the entry stays held, and renewed, until the write commits.
/// </summary>
internal sealed class JobFinalizer(
    Execution execution,
    CancelJobHandler cancelJob,
    WorkerWakeupPublisher wakeupPublisher,
    JobsOptions options,
    WorkerContext context,
    InFlightJobRegistry registry,
    ILogger log,
    ActaMetrics? metrics
)
{
    /// <summary>Enough writers to keep a database busy, few enough that a batch forms when the writes fall behind.</summary>
    public const int LoopCount = 4;

    // One second doubling to a thirty-second ceiling, on the runtime's one retry curve: a dropped
    // connection costs almost nothing, and a failover measured in minutes is ridden out without
    // hammering the provider that is coming back. A quarter of jitter keeps a fleet whose writes failed
    // together from coming back in lockstep.
    private static readonly Backoff RepeatBackoff = Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)).WithJitter(0.25);

    // Warning while a blip is still the likely story, Error once the failure has outlived one.
    private const int WarningTries = 3;

    // Re-arm delay for a claim handed back unrun, in whole seconds (the store verb's unit).
    // SafetyPollInterval is the honest unit for it: the option is documented as the bound on how long
    // a Ready row waits to be found by a process this one shares no wakeup transport with, and a
    // process that carries the missing handler is exactly who has to pick this job up. Validation
    // keeps it at or above one second, so the delay never rounds to zero.
    private readonly int _handBackDelaySeconds = (int)Math.Ceiling(options.SafetyPollInterval.TotalSeconds);

    /// <summary>The most completions one call writes: commit cost has sharp diminishing returns past about a hundred.</summary>
    public const int MaxBatch = 100;

    private int WorkerId => context.ResolveWorker(context.WorkerRegistration!.NamespaceName).WorkerId;

    /// <summary>
    /// One of the worker's finalizer loops: finalizes each completion together with whatever else is already waiting,
    /// until the channel is completed and empty, so a batch forms only when the writes fall behind. A fault ends its batch.
    /// </summary>
    public async Task RunLoopAsync(CancellationToken hostCt)
    {
        var reader = registry.Completed.Reader;
        try
        {
            await foreach (var first in reader.ReadAllAsync(hostCt))
            {
                List<InFlightJob> entries = [first];
                while (entries.Count < MaxBatch && reader.TryRead(out var next))
                {
                    entries.Add(next);
                }

                try
                {
                    await FinalizeAsync(entries, hostCt);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !hostCt.IsCancellationRequested)
                {
                    log.LogError(ex, "WorkerRuntime: finalizer faulted on {Count} completions.", entries.Count);
                }
            }
        }
        catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
        {
            // A hard stop: completions still queued re-run after recovery, as an interrupted write does.
        }
    }

    /// <summary>Writes <paramref name="entry"/>'s completion on its own and removes the entry.</summary>
    public async Task<RunOnceStatus> FinalizeAsync(InFlightJob entry, CancellationToken hostCt)
    {
        try
        {
            return await LandAsync(Plan(entry), batched: null, hostCt);
        }
        finally
        {
            registry.Remove(entry);
        }
    }

    /// <summary>Writes the plain completions in one call and the rest per job, then removes every entry.</summary>
    private async Task FinalizeAsync(List<InFlightJob> entries, CancellationToken hostCt)
    {
        try
        {
            var writes = entries.Select(Plan).ToList();
            var plain = writes.Where(write => write.Plain).ToDictionary(write => write.Entry.Key);
            if (plain.Count > 0)
            {
                var batch = new CompleteExecutionsCommand(
                    WorkerId,
                    [.. plain.Values.Select(write => CompleteExecutionsValues.From(write.Command))]
                );
                var answer = await RepeatAsync(
                    token => execution.CompleteExecutionsAsync(batch, token),
                    log,
                    batch.Completions[0].JobId,
                    hostCt,
                    metrics
                );
                foreach (var row in answer.Rows)
                {
                    // Completed, NotOwner, and AlreadyTerminal share their values with the per-job write's answers, and
                    // the batch takes no job with a next run to report or a parent to release.
                    await LandAsync(
                        plain[(row.JobId, row.ExecutionNumber)],
                        row.Action == CompleteExecutionsAction.PerJob
                            ? null
                            : new((CompleteExecutionAction)row.Action, row.FinalStatusCode, null, row.DbNowUtc, false),
                        hostCt
                    );
                }
            }

            foreach (var write in writes.Where(write => !write.Plain))
            {
                await LandAsync(write, batched: null, hostCt);
            }
        }
        finally
        {
            foreach (var entry in entries)
            {
                registry.Remove(entry);
            }
        }
    }

    /// <summary>Plans <paramref name="entry"/>'s write and the status it reports once the write lands.</summary>
    private PlannedWrite Plan(InFlightJob entry)
    {
        var job = entry.Claim;
        var completion = entry.Completion!;
        if (entry.Descriptor is not { } descriptor)
        {
            return new(entry, completion, HandBack(job, WorkerId, completion), RunOnceStatus.Rearmed);
        }

        var retry = PlanRetry(descriptor, job, entry.Fire, completion);

        // Deadline reschedule guard (Strict): if the next retry would land after the deadline,
        // terminate Cancelled+JobDeadlineExceeded via the handler status instead of re-arming.
        // Budget is left untouched; the handler-status shape handles completion. Same worker-clock
        // comparison as the admission check: deliberate and skew-guarded, not a DB read.
        if (
            retry.InBudget
            && descriptor.DeadlineBehavior == DeadlineBehaviorCode.Strict
            && entry.Context?.TimeUntilDeadline is { } untilDeadline
            && untilDeadline < TimeSpan.FromSeconds(retry.DelaySeconds)
        )
        {
            completion = completion with
            {
                Outcome = CompletionKind.Cancelled,
                Reason = EventReasonCode.JobDeadlineExceeded,
                ReasonMessage = "Next retry would exceed the job deadline.",
                HandlerStatusCode = JobStatusCode.Cancelled,
            };
        }

        var status = completion.Outcome switch
        {
            // A failed one-shot that re-armed for another attempt: the row is Ready, not Failed.
            CompletionKind.Failed when retry.InBudget => RunOnceStatus.Rearmed,
            CompletionKind.Succeeded or CompletionKind.Cancelled => RunOnceStatus.Completed,
            CompletionKind.Rescheduled or CompletionKind.Suspended or CompletionKind.Paused => RunOnceStatus.Rearmed,
            _ => RunOnceStatus.Failed,
        };
        return new(entry, completion, BuildCommand(descriptor, job, WorkerId, entry.Fire, completion, retry), status);
    }

    /// <summary>
    /// Writes <paramref name="write"/> per job unless a batch already answered it, then what follows: a skip when the
    /// row no longer matches (NotOwner, AlreadyTerminal), since the entry can outlive its lease while it waits;
    /// otherwise the wakes, descendant cancel, and metric its final state calls for, none of them for a hand-back,
    /// since every claim loop re-polls within its delay.
    /// </summary>
    private async Task<RunOnceStatus> LandAsync(PlannedWrite write, CompleteExecutionResponse? batched, CancellationToken hostCt)
    {
        var (entry, completion, command, status) = write;
        var job = entry.Claim;
        var ns = context.WorkerRegistration!.NamespaceName;
        var complete =
            batched ?? await RepeatAsync(token => execution.CompleteExecutionAsync(command, token), log, job.JobId, hostCt, metrics);
        if (complete.Action != CompleteExecutionAction.Completed)
        {
            // An external cancel, cascade, or stolen lease moved the row out of execution, or a first try
            // committed and lost its answer, so the compare-and-swap completed no work.
            log.LogInformation(
                "WorkerRuntime: ({Namespace}) job {JobId} completion skipped: ({Outcome}).",
                ns,
                job.JobId,
                complete.Action.ToString()
            );
            return RunOnceStatus.NothingClaimed;
        }

        if (entry.Descriptor is { } descriptor)
        {
            try
            {
                await WakeAsync(complete, job, ns, hostCt);

                // Handler cancel: cancel the descendant subtree as follow-up transactions (the completion
                // above already raised this job's own latch in-routine); maintenance is the crash backstop.
                if (completion.HandlerStatusCode == JobStatusCode.Cancelled)
                {
                    await cancelJob.CancelDescendantsAsync(job.JobId, CancelJobHandler.ParentCancelled, hostCt);
                }

                // reason_code is the failure cause's kebab (null on success).
                metrics?.RecordExecution(
                    ns,
                    descriptor.JobName,
                    OutcomeTag(completion.Outcome),
                    completion.Reason?.Code,
                    completion.DurationMs
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !hostCt.IsCancellationRequested)
            {
                log.LogError(ex, "WorkerRuntime: follow-ups for job {JobId} failed after its completion landed.", job.JobId);
            }
        }

        log.LogInformation("WorkerRuntime: ({Namespace}) job {JobId} -> ({Outcome})", ns, job.JobId, status.ToString());
        return status;
    }

    /// <summary>
    /// Repeats a write an execution cannot afford to lose until it lands or the worker stops: an abandoned completion
    /// leaves the row Executing until its lease lapses, and recovery then charges a failure and runs the job again.
    /// The write is a compare-and-swap, so repeating is safe, and every failure is repeated, since a defect that loops
    /// loudly costs less than a finished execution charged and run twice. The token bounds only the waits between tries.
    /// </summary>
    internal static async Task<T> RepeatAsync<T>(
        Func<CancellationToken, Task<T>> write,
        ILogger log,
        long jobId,
        CancellationToken ct,
        ActaMetrics? metrics = null,
        TimeSpan? firstDelay = null
    )
    {
        // A zero first delay is the test seam: the curve stays at zero however many tries it takes.
        var backoff = firstDelay is { } first
            ? Backoff.Exponential(first, first > RepeatBackoff.MaxDelay ? first : RepeatBackoff.MaxDelay)
            : RepeatBackoff;
        var counted = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await write(ct);
                }
                // A cancelled token is the only exit that is not a landed write: the worker is stopping,
                // so whatever the write threw propagates as it is and the row falls to recovery.
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var delay = TimeSpan.FromSeconds(BackoffSchedule.ComputeDelaySeconds(attempt, backoff));
                    // Diagnostics cannot be allowed to end the repeat: a metrics listener or a logger that
                    // throws would abandon the row the repeat exists to keep.
                    try
                    {
                        if (!counted)
                        {
                            counted = true;
                            metrics?.RecordUnsettledCompletion(1);
                        }
                        log.Log(
                            attempt <= WarningTries ? LogLevel.Warning : LogLevel.Error,
                            ex,
                            "WorkerRuntime: completion write for job {JobId} failed on try {Count}; retrying in {DurationMs}ms until it lands or this worker stops.",
                            jobId,
                            attempt,
                            (long)delay.TotalMilliseconds
                        );
                    }
                    catch (Exception)
                    {
                        // Swallowed on purpose; the write's own failure is the one being handled.
                    }

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopped mid-wait: report the write's own failure, not the stop. The stop says why
                        // the repeat ended; the failure says why the row is still Executing.
                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }
                }
            }
        }
        finally
        {
            if (counted)
            {
                try
                {
                    metrics?.RecordUnsettledCompletion(-1);
                }
                catch (Exception)
                {
                    // A listener that throws must not turn a landed write into a failure.
                }
            }
        }
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
    /// Publishes on the FINAL state the routine reports, never the category the caller guessed: a signal that arrived
    /// Set while the handler ran lands the job Ready inside complete_execution, which no other publish site sees. A
    /// final Ready wakes the namespace's claim loops (due now is WorkAvailable, a run time ahead of db_now is
    /// HorizonChanged so sleeping loops re-read their horizon); a final terminal status wakes the job's completion
    /// channel for a colocated RunAndWaitAsync caller.
    /// </summary>
    private async Task WakeAsync(CompleteExecutionResponse complete, ClaimedJob job, string ns, CancellationToken ct)
    {
        if (complete.FinalStatusCode == (byte)JobStatusCode.Ready)
        {
            var wakeReason =
                complete.FinalNextRunAtUtc is { } nextRun && nextRun > complete.DbNowUtc
                    ? WorkerWakeupReason.HorizonChanged
                    : WorkerWakeupReason.WorkAvailable;
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(ns), wakeReason, ct);
        }
        else if (complete.FinalStatusCode is { } finalStatus && ((JobStatusCode)finalStatus).IsTerminal)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(job.JobId), WorkerWakeupReason.JobFinished, ct);
        }

        // The completion handed a lane to its next member inside complete_execution; lanes are namespace-scoped,
        // so the member is claimable in this namespace.
        if (complete.LanePromoted)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(ns), WorkerWakeupReason.WorkAvailable, ct);
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

    /// <summary>One entry's write as planned: the completion after the deadline guard, its command, and its status.</summary>
    private readonly record struct PlannedWrite(
        InFlightJob Entry,
        ExecutionCompletion Completion,
        CompleteExecutionCommand Command,
        RunOnceStatus Status
    )
    {
        /// <summary>A one-shot landing Succeeded or Failed with nothing else to write, so <c>complete_executions</c> takes it.</summary>
        public bool Plain =>
            Command
                is { RescheduleStatusCode: null, HandlerStatusCode: null, FinalStatus: null, WaitSignalName: null, ScheduleAdvances: null };
    }
}
