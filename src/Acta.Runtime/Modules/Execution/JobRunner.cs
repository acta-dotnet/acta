using System.Diagnostics;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// The start-invoke-complete lifecycle of a single attempt: the <c>start_execution</c> CAS, the
/// exclusive-key lock (taken after the CAS, released when the handler finishes; a loser re-arms
/// Ready after the fixed bounce delay), input deserialization, the generator-emitted
/// <c>descriptor.Invoker</c> invocation wrapped in the registered pipeline behaviors, result
/// serialization, and the <c>complete_execution</c> write (including recurring-completion outcome
/// math). With no behaviors registered, dispatch uses <c>descriptor.Invoker</c> directly.
/// </summary>
internal sealed class JobRunner(
    IJobStore jobStore,
    IExecutionStore execution,
    IJobPayloadSerializerRegistry serializers,
    IOptions<JobsOptions> options,
    JobBehaviorPipeline pipeline,
    WorkerWakeupPublisher wakeupPublisher,
    ILogger? log = null,
    JobMetrics? metrics = null,
    CompletionSink? completionSink = null
)
{
    private readonly IJobStore _jobStore = jobStore;
    private readonly IExecutionStore _execution = execution;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly JobBehaviorPipeline _pipeline = pipeline;
    private readonly WorkerWakeupPublisher _wakeupPublisher = wakeupPublisher;
    private readonly CompletionSink? _completionSink = completionSink;
    private readonly ExecutionProfile _executionProfile = options.Value.ExecutionProfile;
    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly int _exclusiveKeyBounceDelaySeconds = options.Value.ExclusiveKeyBounceDelaySeconds;
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly JobMetrics? _metrics = metrics;

    public async Task<RunOnceOutcome> RunAsync(
        IServiceProvider attemptServices,
        JobDescriptor descriptor,
        ClaimedJob job,
        RuntimeJobContext jobContext,
        int workerId,
        bool isRecurring,
        RecurringFireOutcome? fireOutcome,
        bool alreadyStarted,
        CancellationToken ct
    )
    {
        var start = alreadyStarted
            ? StartExecutionAction.Started
            : await _execution.StartExecutionAsync(job.JobId, workerId, job.ExecutionNumber, job.Version, _leaseTtlSeconds, ct);
        if (start != StartExecutionAction.Started)
        {
            // The claim was lost before execution began: reclaimed (lease expiry), reassigned, or
            // moved out of Dispatched by an operator control verb between claim and start. The CAS
            // guard (incl. the claim-time version) means we mutated nothing; clean skip, let the row
            // be re-claimed next tick.
            _log.LogInformation(
                "WorkerRuntime: lost claim on job {JobId} (en={ExecutionNumber}) before start: {Action}; skipping.",
                job.JobId,
                job.ExecutionNumber,
                start
            );
            return RunOnceOutcome.NothingClaimed;
        }

        // Admission deadline check: a Strict job that is already past its deadline at the moment
        // execution would start is terminated Cancelled without invoking the handler. Advisory is a
        // no-op here; the handler runs regardless and the caller decides what to do.
        // The deadline anchor is DB-stamped (job.created_at_utc); comparing it against the worker
        // clock here is deliberate and bounded by the worker-init clock-skew guard, trading a small
        // skew sensitivity for not paying a DB round-trip on every admission.
        var deadlineHitAtAdmission =
            descriptor.DeadlineBehavior == DeadlineBehaviorCode.Strict
            && jobContext.DeadlineAtUtc is { } admitDue
            && admitDue <= DateTime.UtcNow;

        ExecutionOutcome outcome;
        JobEventReasonCode? failureReason = null;
        string? failureMessage = null;
        var resultFormatId = (byte)0;
        ReadOnlyMemory<byte> resultBytes = ReadOnlyMemory<byte>.Empty;
        int? rescheduleDelaySeconds = null;
        DateTime? rescheduleResumeAtUtc = null;
        string? waitSignalName = null;
        byte? handlerStatusCode = null;
        var durationMs = 0;

        // Exclusive-key admission: the mutex is a lock-store row taken here, after the start CAS
        // proved ownership, and held only while the handler runs. A loser skips the handler and
        // settles the attempt as a budget-neutral re-arm with the fixed bounce delay (the contention
        // throttle): mutual exclusion of execution only, no per-key ordering.
        var exclusiveKeyBounced = false;
        if (!deadlineHitAtAdmission && job.ExclusiveKey is { } exclusiveKey)
        {
            exclusiveKeyBounced = !await jobContext.TryAcquireExclusiveKeyLockAsync(exclusiveKey, ct);
            if (exclusiveKeyBounced)
            {
                _log.LogDebug(
                    "WorkerRuntime: job {JobId} bounced: exclusive key '{ExclusiveKey}' is held; re-armed Ready in {DelaySeconds}s.",
                    job.JobId,
                    exclusiveKey,
                    _exclusiveKeyBounceDelaySeconds
                );
            }
        }

        if (!deadlineHitAtAdmission && !exclusiveKeyBounced)
        {
            var sw = Stopwatch.StartNew();
            var inputDeserialized = false;

            try
            {
                var requestObject = DeserializeInput(descriptor, job);
                inputDeserialized = true;
                ValueTask<JobHandlerInvocationResult> handlerInvocation()
                {
                    // CLI 'jobs debug --break': raise the debugger and stop right here, so step-into
                    // lands in the user's handler. Only ever set on the CLI debug path (DebugBreak).
                    if (DebugBreak.Requested)
                    {
                        if (!Debugger.IsAttached)
                        {
                            Debugger.Launch();
                        }
                        Debugger.Break();
                    }
                    return descriptor.Invoker(attemptServices, requestObject, jobContext, jobContext.CancellationToken);
                }
                var chain = _pipeline.Build(attemptServices, requestObject, jobContext, jobContext.CancellationToken, handlerInvocation);

                var invocation = await chain();
                outcome = ExecutionOutcome.Succeeded;

                if (invocation.HasResult)
                {
                    // Null contract: a payload value is never null. A handler returning null from Task<T> is a
                    // bug, surfaced loudly as Failed; "no result" is expressed by a Task/void handler, not by
                    // returning null. Thrown inside the try so it flows through the normal handler-fault path.
                    if (invocation.Result is null)
                    {
                        throw new InvalidOperationException(
                            "Handler returned null for a non-null result type. Acta results cannot be null: "
                                + "use Task for no result, or wrap optional data in a non-null object."
                        );
                    }
                }

                if (
                    invocation.HasResult
                    && descriptor.OutputPayloadFormat is { } outFormat
                    && descriptor.SerializeOutput is { } serializeOutput
                )
                {
                    var serializer = _serializers.Resolve(outFormat.Id);
                    var payload = serializeOutput(serializer, invocation.Result);
                    resultFormatId = payload.Format.Id;
                    resultBytes = payload.Data;

                    if (resultBytes.Length > _maxInlinePayloadBytes)
                    {
                        // The job succeeded, so it is not failed over the size of what it returned. But the
                        // cap is a cap: the body is dropped rather than persisted, leaving the row in the
                        // existing "no result" shape (format 0, NULL) with job.result-oversized recording
                        // why. A typed read of the missing result throws rather than handing back a default.
                        _log.LogWarning(
                            "Handler result for job '{JobName}' ({JobId}) is {Bytes} bytes, exceeding the {Cap}-byte MaxInlinePayloadBytes cap; the result body was dropped.",
                            descriptor.JobName,
                            job.JobId,
                            resultBytes.Length,
                            _maxInlinePayloadBytes
                        );
                        resultFormatId = 0;
                        resultBytes = ReadOnlyMemory<byte>.Empty;
                        failureReason = JobEventReasonCode.JobResultOversized;
                    }
                }
            }
            catch (JobControlException control)
            {
                // A deliberate control signal: reschedule or durable sleep. Caught before cancellation /
                // generic failure so it is never mis-recorded as Failed. Unknown subclasses are rethrown so
                // a subsequent control verb can never silently fall into the re-arm path.
                switch (control)
                {
                    case RescheduleJobException reschedule:
                        outcome = ExecutionOutcome.Rescheduled;
                        failureReason = JobEventReasonCode.JobHandlerRescheduled;
                        failureMessage = reschedule.Message.Truncate(ActaTextLimits.ReasonMessage);
                        if (reschedule.Delay is { } delay)
                        {
                            rescheduleDelaySeconds = (int)delay.TotalSeconds;
                        }
                        else if (reschedule.ResumeAtUtc is { } resume)
                        {
                            rescheduleResumeAtUtc = resume.UtcDateTime;
                        }
                        break;
                    case SuspendSignal suspend:
                        outcome = ExecutionOutcome.Suspended;
                        failureReason = JobEventReasonCode.JobHandlerSuspended;
                        failureMessage = suspend.ReasonMessage.Truncate(ActaTextLimits.ReasonMessage);
                        rescheduleResumeAtUtc = suspend.ResumeAtUtc;
                        break;
                    case StepRetrySignal stepRetry:
                        // An inline step still awaits its retry instant on replay, or failed in budget and scheduled a
                        // retry. Re-arm budget-neutral via the existing Rescheduled path (failure_count
                        // untouched) at the step's next_retry_at_utc; only the reason differs.
                        outcome = ExecutionOutcome.Rescheduled;
                        failureReason = JobEventReasonCode.JobStepRetryScheduled;
                        failureMessage = (stepRetry.ReasonMessage ?? $"Step '{stepRetry.StepName}' retry scheduled.").Truncate(
                            ActaTextLimits.ReasonMessage
                        );
                        rescheduleResumeAtUtc = stepRetry.ResumeAtUtc;
                        break;
                    case SignalSuspendSignal signalSuspend:
                        // Signal wait with no Set slot: re-arm as Suspended (no due time). complete_execution
                        // locks the named slot and lands Ready instead if a raise won the race.
                        outcome = ExecutionOutcome.Suspended;
                        failureReason = JobEventReasonCode.JobHandlerSuspended;
                        failureMessage = (signalSuspend.ReasonMessage ?? $"Signal '{signalSuspend.SignalName}' pending.").Truncate(
                            ActaTextLimits.ReasonMessage
                        );
                        waitSignalName = signalSuspend.SignalName;
                        break;
                    case HandlerFailException fail:
                        // Deliberate terminal Failed: no retry, budget untouched. Distinct from an unhandled
                        // exception (UnhandledException reason); both land Failed but the reason tells them apart.
                        outcome = ExecutionOutcome.Failed;
                        failureReason = JobEventReasonCode.JobHandlerFailed;
                        failureMessage = fail.ReasonMessage.Truncate(ActaTextLimits.ReasonMessage);
                        handlerStatusCode = (byte)JobStatusCode.Failed;
                        break;
                    case HandlerCancelException cancel:
                        outcome = ExecutionOutcome.Cancelled;
                        failureReason = JobEventReasonCode.JobHandlerCancelled;
                        failureMessage = cancel.ReasonMessage.Truncate(ActaTextLimits.ReasonMessage);
                        handlerStatusCode = (byte)JobStatusCode.Cancelled;
                        break;
                    case HandlerPauseException pause:
                        outcome = ExecutionOutcome.Paused;
                        failureReason = JobEventReasonCode.JobHandlerPaused;
                        failureMessage = pause.ReasonMessage.Truncate(ActaTextLimits.ReasonMessage);
                        handlerStatusCode = (byte)JobStatusCode.Paused;
                        break;
                    default:
                        throw;
                }
            }
            catch (OperationCanceledException) when (jobContext.CancellationToken.IsCancellationRequested)
            {
                if (ct.IsCancellationRequested)
                {
                    // Worker shutdown cancelled this attempt: the worker token itself is cancelled, not
                    // just the per-attempt token (an external operator cancel or a stolen lease cancels
                    // only the attempt token via the heartbeat, leaving ct live). Leave the row Executing
                    // and write NOTHING - sys.recovery reclaims it after the lease lapses, honoring the
                    // documented worker-shutdown -> retry/reclaim contract. Completing here would be wrong
                    // on every profile and actively harmful on Bulk, whose group-commit buffers under
                    // CancellationToken.None and would finalize this never-failed attempt as terminal Failed.
                    _log.LogInformation(
                        "WorkerRuntime: job {JobId} interrupted by worker shutdown; left Executing for recovery.",
                        job.JobId
                    );
                    return RunOnceOutcome.NothingClaimed;
                }

                outcome = ExecutionOutcome.Failed;
                if (jobContext.AttemptTimedOut)
                {
                    // The per-attempt execution timeout fired. Record it distinctly and let the retry budget
                    // below decide re-arm vs terminal; a timeout is a retryable failure like an exception.
                    failureReason = JobEventReasonCode.JobExecutionTimeout;
                    failureMessage = "Execution exceeded the configured timeout.";
                    _log.LogWarning("Handler for job '{JobName}' ({JobId}) exceeded its execution timeout.", descriptor.JobName, job.JobId);
                }
                else
                {
                    // The attempt token was cancelled without a timeout: an external cancel, a stolen
                    // lease, the watchdog's lease-runway margin, or a lost handler lock. The first two
                    // leave a row this worker no longer owns (the CAS below no-ops), but the watchdog
                    // and lock-loss cancel while the row is still Executing and still leased here, so
                    // a reason-less completion would land terminal Failed on a recoverable job. Submit
                    // a retryable failure instead: owned rows re-arm under the failure budget, unowned
                    // rows still no-op. Writing (not skipping) matters because a live worker's heartbeat
                    // renews every row it leases, so a skipped write could leave a zombie Executing row
                    // renewed forever.
                    failureReason = JobEventReasonCode.JobAttemptAborted;
                    failureMessage = "Attempt aborted: the lease or a held lock could no longer be guaranteed.";
                    _log.LogWarning("WorkerRuntime: job {JobId} attempt aborted mid-flight; handler stopped cooperatively.", job.JobId);
                }
            }
            catch (StepOwnershipLostException ownershipLost)
            {
                // A step completion CAS matched no row: a concurrent execution re-claimed this job
                // (bumping runtimes.version), so this attempt is a zombie. Stop cooperatively, same as a stolen
                // lease; the complete CAS below no-ops against the row the winner now owns.
                outcome = ExecutionOutcome.Failed;
                _log.LogInformation(
                    "WorkerRuntime: job {JobId} lost step '{StepName}' ownership mid-flight; another execution owns it. Stopping cooperatively.",
                    job.JobId,
                    ownershipLost.StepName
                );
            }
            catch (StepInterruptedException interrupted)
            {
                // An AtMostOnce step was re-entered before its outcome was recorded and the handler did
                // not catch it. Land terminal Failed immediately via the same handler-status path as
                // ctx.FailAsync: no retry (JobStepInterrupted is non-retryable), budget untouched, so the
                // parent is never replayed back into the interrupted step. The distinct reason preserves
                // the "outcome unknown" story for jobs explain / the timeline.
                outcome = ExecutionOutcome.Failed;
                failureReason = JobEventReasonCode.JobStepInterrupted;
                failureMessage = interrupted.Message.Truncate(ActaTextLimits.ReasonMessage);
                handlerStatusCode = (byte)JobStatusCode.Failed;
                _log.LogWarning(
                    "Handler for job '{JobName}' ({JobId}) did not handle StepInterruptedException for step '{StepName}'; failing terminally.",
                    descriptor.JobName,
                    job.JobId,
                    interrupted.StepName
                );
            }
            catch (Exception ex)
            {
                outcome = ExecutionOutcome.Failed;
                if (ex is NotImplementedException or NotSupportedException)
                {
                    // A programming error retrying cannot fix: land terminal Failed through the same
                    // completion branch as ctx.FailAsync, leaving the retry budget untouched.
                    failureReason = JobEventReasonCode.JobNonRetryableException;
                    handlerStatusCode = (byte)JobStatusCode.Failed;
                }
                else
                {
                    failureReason = JobEventReasonCode.JobUnhandledException;
                }
                failureMessage = (
                    inputDeserialized ? ex.Message : $"Input deserialization failed ({ex.GetType().Name}): {ex.Message}"
                ).Truncate(ActaTextLimits.ReasonMessage);
                if (inputDeserialized)
                {
                    _log.LogWarning(
                        ex,
                        "Handler for job '{JobName}' ({JobId}) threw; transitioning to Failed.",
                        descriptor.JobName,
                        job.JobId
                    );
                }
                else
                {
                    _log.LogWarning(
                        ex,
                        "Input deserialization for job '{JobName}' ({JobId}) failed; transitioning the attempt to Failed.",
                        descriptor.JobName,
                        job.JobId
                    );
                }
            }
            finally
            {
                sw.Stop();
                // Mutual exclusion covers the handler, not the completion write; runs on every exit
                // path (success, catches, the worker-shutdown return, control rethrows). Release is
                // best-effort and non-throwing; failure self-heals via the lock's TTL while durable
                // completion continues below.
                await jobContext.ReleaseExclusiveKeyLockAsync(CancellationToken.None);
            }

            durationMs = (int)Math.Min(sw.ElapsedMilliseconds, int.MaxValue);
        }
        else if (deadlineHitAtAdmission)
        {
            // Deadline already passed at admission: terminate without invoking the handler.
            outcome = ExecutionOutcome.Cancelled;
            failureReason = JobEventReasonCode.JobDeadlineExceeded;
            failureMessage = "Job passed its deadline before execution started.";
            handlerStatusCode = (byte)JobStatusCode.Cancelled;
            _log.LogInformation("WorkerRuntime: job {JobId} overdue at admission; cancelling without running the handler.", job.JobId);
        }
        else
        {
            // Exclusive-key bounce: settle the attempt as a budget-neutral re-arm with the fixed delay.
            outcome = ExecutionOutcome.Rescheduled;
            failureReason = JobEventReasonCode.JobExclusiveKeyHeld;
            failureMessage = "Exclusive key lock held by another execution.";
            rescheduleDelaySeconds = _exclusiveKeyBounceDelaySeconds;
        }

        // Resolved once and passed on every completion shape; complete_execution stamps it onto
        // runtimes.retention_until_utc only at a terminal landing (Done/Failed/Cancelled) and ignores it on
        // re-arm / suspend / pause. Per-definition policy with the framework default as the fallback.
        var retentionSeconds = descriptor.JobRetentionSeconds ?? JobDefinitionRegistration.DefaultJobRetentionSeconds;

        // One-shot failure retry math, hoisted so the deadline reschedule guard and the re-arm branch share it.
        short failedAttempted = 0;
        int failedRetryDelaySeconds = 0;
        var failedInBudget = false;
        if (outcome == ExecutionOutcome.Failed && IsRetryable(failureReason))
        {
            failedAttempted = (short)(job.FailureCount + 1);
            failedInBudget = failedAttempted < descriptor.MaxAttempts;
            if (failedInBudget)
            {
                var backoff = Backoff.Parse(descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression);
                failedRetryDelaySeconds = BackoffSchedule.ComputeDelaySeconds(failedAttempted, backoff);

                // Deadline reschedule guard (Strict): if the next retry would land after the deadline,
                // terminate Cancelled+JobDeadlineExceeded via handlerStatusCode instead of re-arming.
                // Budget is left untouched; the handler-status branch handles completion. Same worker-
                // clock comparison as the admission check above: deliberate and skew-guarded, not a DB read.
                if (
                    descriptor.DeadlineBehavior == DeadlineBehaviorCode.Strict
                    && jobContext.DeadlineAtUtc is { } retryDue
                    && DateTime.UtcNow.AddSeconds(failedRetryDelaySeconds) > retryDue
                )
                {
                    outcome = ExecutionOutcome.Cancelled;
                    failureReason = JobEventReasonCode.JobDeadlineExceeded;
                    failureMessage = "Next retry would exceed the job deadline.";
                    handlerStatusCode = (byte)JobStatusCode.Cancelled;
                }
            }
        }

        CompleteExecutionRequest completeCommand;
        var retryRearmed = false;
        if (outcome is ExecutionOutcome.Rescheduled or ExecutionOutcome.Suspended)
        {
            // Re-arm takes the non-recurring shape regardless of isRecurring so recurring schedule
            // cursors are never advanced; no result payload is written (budget-neutral re-arm).
            var rescheduleStatus = (byte)(
                outcome == ExecutionOutcome.Suspended ? ExecutionStatusCode.Suspended : ExecutionStatusCode.Rescheduled
            );
            completeCommand = new CompleteExecutionRequest(
                job.JobId,
                workerId,
                job.ExecutionNumber,
                outcome,
                0,
                ReadOnlyMemory<byte>.Empty,
                failureReason,
                failureMessage,
                durationMs
            )
            {
                RescheduleStatusCode = rescheduleStatus,
                RescheduleDelaySeconds = rescheduleDelaySeconds,
                RescheduleResumeAtUtc = rescheduleResumeAtUtc,
                WaitSignalName = waitSignalName,
                RetentionSeconds = retentionSeconds,
            };
        }
        else if (handlerStatusCode is { } handlerStatus)
        {
            // Handler fail/cancel/pause: a deliberate terminal/hold decision. Takes the non-recurring
            // shape regardless of isRecurring (like the re-arm branch) so a recurring slot's schedule
            // cursors are never advanced; the whole Job stops. No result payload; budget untouched.
            completeCommand = new CompleteExecutionRequest(
                job.JobId,
                workerId,
                job.ExecutionNumber,
                outcome,
                0,
                ReadOnlyMemory<byte>.Empty,
                failureReason,
                failureMessage,
                durationMs
            )
            {
                HandlerStatusCode = handlerStatus,
                RetentionSeconds = retentionSeconds,
            };
        }
        else if (isRecurring && fireOutcome is { } fire)
        {
            var (finalStatus, failureCount, reasonCode, reasonMessage) = ComputeRecurringOutcome(
                outcome,
                job,
                descriptor,
                fire,
                failureReason,
                failureMessage
            );

            completeCommand = new CompleteExecutionRequest(
                job.JobId,
                workerId,
                job.ExecutionNumber,
                outcome,
                resultFormatId,
                resultBytes,
                reasonCode,
                reasonMessage,
                durationMs,
                ScheduleAdvances: fire.Advances,
                FinalStatus: finalStatus,
                JobNextRunAtUtc: fire.SlotMinNextRunAtUtc,
                FailureCount: failureCount,
                RecurringResultCap: descriptor.RecurringResultCap
            )
            {
                RetentionSeconds = retentionSeconds,
            };
        }
        else if (outcome == ExecutionOutcome.Failed && IsRetryable(failureReason))
        {
            // One-shot failure (unhandled exception / execution timeout / aborted attempt): honor the retry budget.
            // In budget: re-arm to Ready with a backoff delay and persist the bumped failure_count.
            // Exhausted: terminal Failed, keeping the final count.
            // A Strict deadline guard above may have already converted this to Cancelled+handlerStatusCode,
            // routing to the handler-status branch before this one is reached.
            if (failedInBudget)
            {
                completeCommand = new CompleteExecutionRequest(
                    job.JobId,
                    workerId,
                    job.ExecutionNumber,
                    outcome,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    failureReason,
                    failureMessage,
                    durationMs
                )
                {
                    RescheduleStatusCode = (byte)ExecutionStatusCode.Rescheduled,
                    RescheduleDelaySeconds = failedRetryDelaySeconds,
                    FailureCount = failedAttempted,
                    RetentionSeconds = retentionSeconds,
                };
                retryRearmed = true;
            }
            else
            {
                completeCommand = new CompleteExecutionRequest(
                    job.JobId,
                    workerId,
                    job.ExecutionNumber,
                    outcome,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    failureReason,
                    failureMessage,
                    durationMs
                )
                {
                    FailureCount = failedAttempted,
                    RetentionSeconds = retentionSeconds,
                };
            }
        }
        else
        {
            completeCommand = new CompleteExecutionRequest(
                job.JobId,
                workerId,
                job.ExecutionNumber,
                outcome,
                resultFormatId,
                resultBytes,
                failureReason,
                failureMessage,
                durationMs
            )
            {
                RetentionSeconds = retentionSeconds,
            };
        }

        // Bulk profile: a plain terminal completion (Done/Failed, no re-arm / handler-control / recurring /
        // signal branch) is buffered for group commit instead of committed per job. The handler already
        // ran; the slot frees now and the flusher durably finalizes the batch and publishes the deferred
        // wakeups. Everything needing a cross-row side effect or a control branch falls through to the
        // per-job complete_execution below with its full wakeup/cascade handling.
        if (
            _completionSink is { } sink
            && _executionProfile == ExecutionProfile.Bulk
            && completeCommand is { FinalStatus: null, RescheduleStatusCode: null, HandlerStatusCode: null, WaitSignalName: null }
        )
        {
            // No metric here: the sink records "acta.executions" when the flush durably finalizes the
            // row, so Bulk counts what the store confirmed, same as the post-CAS emit below.
            await sink.EnqueueAsync(
                new BufferedCompletion(completeCommand, jobContext.JobNamespace, descriptor.JobName, job.JobId, resultBytes.Length)
            );
            return outcome switch
            {
                ExecutionOutcome.Succeeded or ExecutionOutcome.Cancelled => RunOnceOutcome.Completed,
                _ => RunOnceOutcome.Failed,
            };
        }

        var complete = await _execution.CompleteExecutionAsync(completeCommand, ct);

        if (complete.Action != CompleteExecutionAction.Completed)
        {
            // An external cancel, cascade, or stolen lease moved the row out of execution while the
            // handler ran, so the compare-and-swap completed no work. A terminal row is always a
            // clean skip because a fast handler can finish before heartbeat cancellation reaches the
            // attempt token. Losing ownership without a cancelled token is a genuine anomaly.
            return complete.Action == CompleteExecutionAction.AlreadyTerminal || jobContext.CancellationToken.IsCancellationRequested
                ? RunOnceOutcome.NothingClaimed
                : throw new InvalidOperationException($"CompleteExecution for job {job.JobId} returned {complete.Action}.");
        }

        // Publish on the FINAL state the routine reports, never on the completion category the caller
        // guessed: a signal that arrived Set while the handler ran lands the job Ready inside
        // complete_execution, and no other publish site can see that transition. A final Ready wakes
        // the namespace's claim loops: due-now (immediate retry, signal release) is WorkAvailable, a
        // run time ahead of db_now (backoff retry, recurring roll-over) is HorizonChanged so sleeping
        // loops re-read their horizon. A final TERMINAL status wakes the job's completion channel so
        // an ExecuteAndWaitAsync caller colocated with this worker observes the outcome without waiting out
        // its poll interval.
        if (complete.FinalStatusCode == (byte)JobStatusCode.Ready)
        {
            var wakeReason =
                complete.FinalNextRunAtUtc is { } nextRun && nextRun > complete.DbNowUtc
                    ? WorkerWakeupReason.HorizonChanged
                    : WorkerWakeupReason.WorkAvailable;
            await _wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(jobContext.JobNamespace), wakeReason, ct);
        }
        else if (complete.FinalStatusCode is { } finalStatus && ((JobStatusCode)finalStatus).IsTerminal)
        {
            await _wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(job.JobId), WorkerWakeupReason.JobFinished, ct);
        }

        // A terminal child's raise flipped its Suspended parent to Ready inside complete_execution.
        // The routine knows only the parent's numeric namespace id, so wake every worker namespace
        // (same trade as the control verbs' PublishControlWakeAsync).
        if (complete.ParentReleased)
        {
            await _wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        // Handler cancel: cancel the descendant subtree as follow-up transactions (the completion
        // above already raised this job's own latch in-routine); maintenance is the crash backstop.
        if (handlerStatusCode == (byte)JobStatusCode.Cancelled)
        {
            foreach (var cancelledId in await CancelDescendants.Run(_execution, _jobStore, job.JobId, ct))
            {
                await _wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(cancelledId), WorkerWakeupReason.JobFinished, ct);
            }
        }

        // One measurement per completed execution: the single emit point both the poll loop and the
        // single-shot path funnel through. reason_code is the failure cause's kebab (null on success).
        _metrics?.RecordExecution(jobContext.JobNamespace, descriptor.JobName, OutcomeTag(outcome), failureReason?.Code, durationMs);

        if (retryRearmed)
        {
            // A failed one-shot that re-armed for another attempt: the row is Ready, not Failed.
            return RunOnceOutcome.Rearmed;
        }

        return outcome switch
        {
            ExecutionOutcome.Succeeded or ExecutionOutcome.Cancelled => RunOnceOutcome.Completed,
            ExecutionOutcome.Rescheduled or ExecutionOutcome.Suspended or ExecutionOutcome.Paused => RunOnceOutcome.Rearmed,
            _ => RunOnceOutcome.Failed,
        };
    }

    // Stable low-cardinality tag value for the executions/duration metrics. Kept off ToString() so an
    // enum rename can't silently rename an operator-facing metric dimension. Internal: the Bulk
    // completion sink emits the same metric at durable finalization and must share the tag values.
    internal static string OutcomeTag(ExecutionOutcome outcome) =>
        outcome switch
        {
            ExecutionOutcome.Succeeded => "succeeded",
            ExecutionOutcome.Failed => "failed",
            ExecutionOutcome.Rescheduled => "rescheduled",
            ExecutionOutcome.Suspended => "suspended",
            ExecutionOutcome.Cancelled => "cancelled",
            ExecutionOutcome.Paused => "paused",
            _ => "unknown",
        };

    // Failures eligible for the one-shot retry budget: an unhandled exception, an execution timeout,
    // or an attempt aborted by lease/lock pressure. A deliberate ctx.FailAsync (HandlerFailed) takes
    // the handler-status path and never retries; a null reason falls through to a terminal completion.
    private static bool IsRetryable(JobEventReasonCode? reason) =>
        reason
            is JobEventReasonCode.JobUnhandledException
                or JobEventReasonCode.JobExecutionTimeout
                or JobEventReasonCode.JobAttemptAborted;

    // Recurring completion outcome, computed in C# from the attempt result and the planned slot MIN.
    // A recurring slot re-arms Ready on failure regardless of the consecutive-failure count: MaxAttempts
    // is the one-off retry budget only and never terminalizes a recurring slot. Exhausted schedules pause
    // regardless of outcome; a success resets the failure counter to zero, a failure bumps it (saturating
    // at short.MaxValue so a long outage cannot overflow it into the negatives).
    internal static (
        JobStatusCode FinalStatus,
        short FailureCount,
        JobEventReasonCode? ReasonCode,
        string? ReasonMessage
    ) ComputeRecurringOutcome(
        ExecutionOutcome outcome,
        ClaimedJob job,
        JobDescriptor descriptor,
        RecurringFireOutcome fire,
        JobEventReasonCode? failureReason,
        string? failureMessage
    )
    {
        // Saturate at short.MaxValue so a long outage cannot overflow the counter into the negatives.
        var failureCount =
            outcome == ExecutionOutcome.Succeeded ? (short)0
            : job.FailureCount >= short.MaxValue ? short.MaxValue
            : (short)(job.FailureCount + 1);

        if (fire.SlotMinNextRunAtUtc is null)
        {
            return (JobStatusCode.Paused, failureCount, JobEventReasonCode.JobSchedulesExhausted, null);
        }

        return (JobStatusCode.Ready, failureCount, null, null);
    }

    private object DeserializeInput(JobDescriptor descriptor, ClaimedJob job)
    {
        if (job.InputFormatId == 0)
        {
            // No-payload descriptors ignore both arguments; NullJobPayloadSerializer keeps the signature uniform.
            return descriptor.DeserializeInput(NullJobPayloadSerializer.Instance, default);
        }

        var serializer = _serializers.Resolve(job.InputFormatId);
        var payload = JobPayload.FromBytes(serializer.Format, job.Input.ToArray());
        return descriptor.DeserializeInput(serializer, payload);
    }
}
