using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// Runs an admitted attempt's handler: input deserialization, the generator-emitted <c>descriptor.Invoker</c>
/// wrapped in the registered pipeline behaviors (the invoker directly when none are registered), and result
/// serialization; then completes the attempt by how the handler ended.
/// </summary>
internal sealed class HandlerInvocation(
    IJobPayloadSerializerRegistry serializers,
    JobBehaviorPipeline pipeline,
    int maxInlinePayloadBytes,
    ILogger log,
    ActaMetrics? metrics
)
{
    /// <summary>
    /// The attempt's completion, or null when nothing is to be written: worker shutdown interrupted the handler and the
    /// row stays Executing for recovery, or a write found the row held by another execution, which owns it now. An
    /// attached debugger stops on an error the handler let escape, never on its control flow, a cancellation or a fence.
    /// Every Acta method that runs a delegate the handler passed, inside its own async body or catch, carries
    /// <see cref="DebuggerDisableUserUnhandledExceptionsAttribute"/> too, so the stop here is the only one.
    /// </summary>
    [DebuggerDisableUserUnhandledExceptions]
    public async Task<ExecutionCompletion?> InvokeAsync(
        IServiceProvider attemptServices,
        JobDescriptor descriptor,
        ClaimedJob job,
        RuntimeJobContext jobContext,
        CancellationToken ct
    )
    {
        var started = Stopwatch.GetTimestamp();
        var inputDeserialized = false;
        ExecutionCompletion completion;
        try
        {
            // An attempt cancelled between the start CAS and here - during a rate wait, or in the
            // instant before - completes through the cancellation path below without invoking a
            // handler that may not take the token.
            jobContext.CancellationToken.ThrowIfCancellationRequested();
            var requestObject = DeserializeInput(descriptor, job);
            inputDeserialized = true;
            var output = await InvokeHandlerAsync(attemptServices, descriptor, requestObject, jobContext);
            if (jobContext.ThrownControl is { } swallowed)
            {
                throw new SwallowedControlException(swallowed);
            }
            completion = Succeeded(descriptor, job, output, jobContext.CancellationToken);
        }
        catch (Exception) when (jobContext.Fenced)
        {
            // However the refusal surfaced, rethrown, swallowed and followed by another error, or turned into a control
            // verb, the attempt ends as a lost lease does.
            return FencedOut(job, jobContext);
        }
        catch (JobControlException control)
        {
            completion = Controlled(control);
        }
        catch (OperationCanceledException) when (jobContext.CancellationToken.IsCancellationRequested)
        {
            if (ct.IsCancellationRequested)
            {
                // Worker shutdown cancelled this attempt: the worker token itself is cancelled, not
                // just the per-attempt token (an external operator cancel or a stolen lease cancels
                // only the attempt token via the heartbeat, leaving ct live). Leave the row Executing
                // and write NOTHING - crash recovery reclaims it after the lease lapses, honoring the
                // documented worker-shutdown -> retry/reclaim contract; completing here would finalize a
                // never-failed attempt as terminal Failed.
                ActaLog.JobInterruptedByShutdown(log, job.JobId);
                return null;
            }

            completion = Cancelled(descriptor, job, jobContext);
        }
        catch (StepOwnershipLostException ownershipLost)
        {
            // A step's completion matched no row, and nothing was recorded for it (StepOwnershipLostException says how
            // that happens): the attempt fails under its budget with the step named.
            Debugger.BreakForUserUnhandledException(ownershipLost);
            completion = new ExecutionCompletion(CompletionKind.Failed)
            {
                Reason = EventReason.AttemptAborted,
                ReasonMessage = ownershipLost.Message.Truncate(ActaTextLimits.EventMessage),
            };
            ActaLog.StepOwnershipLost(log, job.JobId, ownershipLost.StepName);
        }
        catch (StepInterruptedException interrupted)
        {
            // An AtMostOnce step was re-entered before its outcome was recorded and the handler did
            // not catch it. Land terminal Failed immediately via the same handler-status path as
            // ctx.FailAsync: no retry (JobStepInterrupted is non-retryable), budget untouched, so the
            // parent is never replayed back into the interrupted step. The distinct reason preserves
            // the "outcome unknown" story for jobs explain / the timeline.
            Debugger.BreakForUserUnhandledException(interrupted);
            completion = ExecutionCompletion.Terminal(
                CompletionKind.Failed,
                EventReason.StepInterrupted,
                interrupted.Message,
                JobStatusCode.Failed
            );
            ActaLog.StepInterruptUnhandled(log, descriptor.JobName, job.JobId, interrupted.StepName);
        }
        catch (Exception ex)
        {
            // A debugger stops only on an exception that passed through the handler's code. For a swallowed control flow
            // that is the control exception the handler caught, not the one reporting it.
            Debugger.BreakForUserUnhandledException(ex is SwallowedControlException { InnerException: { } swallowed } ? swallowed : ex);
            completion = Faulted(ex, inputDeserialized, descriptor, job);
        }

        if (jobContext.Fenced)
        {
            // The handler swallowed the refusal and ended normally.
            return FencedOut(job, jobContext);
        }

        return completion with
        {
            DurationMs = (int)Math.Min((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, int.MaxValue),
        };
    }

    /// <summary>
    /// A fenced write proved another execution holds the row (docs/sql-internals.md, "Execution fence"), so no
    /// completion of this attempt could apply: none is written and no retry is charged.
    /// </summary>
    private ExecutionCompletion? FencedOut(ClaimedJob job, RuntimeJobContext jobContext)
    {
        ActaLog.AttemptFenced(log, job.JobId, job.ExecutionNumber);
        metrics?.RecordFencedAttempt(jobContext.JobNamespace, jobContext.JobName);
        return null;
    }

    private ValueTask<JobHandlerOutput> InvokeHandlerAsync(
        IServiceProvider attemptServices,
        JobDescriptor descriptor,
        object requestObject,
        RuntimeJobContext jobContext
    )
    {
        ValueTask<JobHandlerOutput> handlerInvocation()
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
        var chain = pipeline.Build(attemptServices, requestObject, jobContext, handlerInvocation, jobContext.CancellationToken);
        return chain();
    }

    /// <summary>The attempt's success, with its result recorded; a result that cannot be recorded throws to InvokeAsync.</summary>
    [DebuggerDisableUserUnhandledExceptions]
    private ExecutionCompletion Succeeded(JobDescriptor descriptor, ClaimedJob job, JobHandlerOutput invocation, CancellationToken attempt)
    {
        var succeeded = new ExecutionCompletion(CompletionKind.Succeeded);
        if (!invocation.HasResult)
        {
            return succeeded;
        }

        // Null contract: a payload value is never null. A handler returning null from Task<T> is a
        // bug, surfaced loudly as Failed; "no result" is expressed by a Task/void handler, not by
        // returning null. The handler has run, so the failure is not retried.
        if (invocation.Result is null)
        {
            throw new ResultNotRecordedException(
                "The handler returned null for a non-null result type. Acta results cannot be null: "
                    + "use Task for no result, or wrap optional data in a non-null object."
            );
        }

        if (descriptor.ResultPayloadFormat is not { } resultFormat || descriptor.SerializeResult is not { } serializeResult)
        {
            return succeeded;
        }

        JobPayload payload;
        try
        {
            payload = serializeResult(serializers.Resolve(resultFormat.Id), invocation.Result);
        }
        catch (Exception ex)
            when (ex is not JobControlException && (ex is not OperationCanceledException || !attempt.IsCancellationRequested))
        {
            throw new ResultNotRecordedException($"The handler's result could not be serialized: {ex.Message}", ex);
        }

        if (payload.Data.Length <= maxInlinePayloadBytes)
        {
            return succeeded with { ResultFormatId = payload.Format.Id, Result = payload.Data };
        }

        // The job succeeded, so it is not failed over the size of what it returned. But the
        // cap is a cap: the body is dropped rather than persisted, leaving the row in the
        // existing "no result" shape (format 0, NULL) with result-oversized recording
        // why. A typed read of the missing result throws rather than handing back a default.
        if (log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.ResultDroppedOverCap(
                log,
                descriptor.JobName,
                job.JobId,
                $"{payload.Data.Length} bytes over the MaxInlinePayloadBytes cap of {maxInlinePayloadBytes} bytes"
            );
        }
        return succeeded with { Reason = EventReason.ResultOversized };
    }

    /// <summary>
    /// A deliberate control signal: reschedule or durable sleep. Caught before cancellation /
    /// generic failure so it is never mis-recorded as Failed. The family is closed, so every member has an arm.
    /// </summary>
    private static ExecutionCompletion Controlled(JobControlException control)
    {
        switch (control)
        {
            case RescheduleJobException reschedule:
                return new ExecutionCompletion(CompletionKind.Rescheduled)
                {
                    Reason = EventReason.HandlerRescheduled,
                    ReasonMessage = reschedule.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    RescheduleDelaySeconds = reschedule.Delay is { } delay ? (int)delay.TotalSeconds : null,
                    RescheduleResumeAtUtc = reschedule.Delay is null ? reschedule.ResumeAtUtc?.UtcDateTime : null,
                };
            case SuspendSignal suspend:
                return new ExecutionCompletion(CompletionKind.Suspended)
                {
                    Reason = EventReason.HandlerSuspended,
                    ReasonMessage = suspend.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    RescheduleResumeAtUtc = suspend.ResumeAtUtc,
                };
            case SignalSuspendSignal signalSuspend:
                // Signal wait with no Set checkpoint: re-arm as Suspended (no due time). complete_executions
                // locks the named checkpoint and lands Ready instead if a raise won the race.
                return new ExecutionCompletion(CompletionKind.Suspended)
                {
                    Reason = EventReason.HandlerSuspended,
                    ReasonMessage = signalSuspend.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    WaitSignalName = signalSuspend.SignalName,
                };
            case WaitTimeoutSignal waitTimeout:
                // A bounded wait outlived the expiration stored on its checkpoint and the handler used a
                // non-Try overload. Terminate Cancelled like the Strict-deadline path: no
                // FailureCount is submitted, so complete_executions' COALESCE leaves the budget
                // untouched, and the terminal landing stamps retention so the job stops being
                // an immortal Suspended row.
                return ExecutionCompletion.Terminal(
                    CompletionKind.Cancelled,
                    EventReason.WaitTimedOut,
                    waitTimeout.ReasonMessage,
                    JobStatusCode.Cancelled
                );
            case HandlerFailException fail:
                // Deliberate terminal Failed: no retry, budget untouched. Distinct from an unhandled
                // exception (JobUnhandledException reason); both land Failed but the reason tells them apart.
                return ExecutionCompletion.Terminal(
                    CompletionKind.Failed,
                    EventReason.HandlerFailed,
                    fail.ReasonMessage,
                    JobStatusCode.Failed
                );
            case HandlerCancelException cancel:
                return ExecutionCompletion.Terminal(
                    CompletionKind.Cancelled,
                    EventReason.HandlerCancelled,
                    cancel.ReasonMessage,
                    JobStatusCode.Cancelled
                );
            case HandlerPauseException pause:
                return ExecutionCompletion.Terminal(
                    CompletionKind.Paused,
                    EventReason.HandlerPaused,
                    pause.ReasonMessage,
                    JobStatusCode.Paused
                );
            default:
                throw new UnreachableException($"Control signal {control.GetType().Name} has no completion.");
        }
    }

    private ExecutionCompletion Cancelled(JobDescriptor descriptor, ClaimedJob job, RuntimeJobContext jobContext)
    {
        if (jobContext.AttemptTimedOut)
        {
            // The per-attempt execution timeout fired. Record it distinctly and let the retry budget
            // decide re-arm vs terminal; a timeout is a retryable failure like an exception.
            if (log.IsEnabled(LogLevel.Warning))
            {
                ActaLog.ExecutionTimedOut(log, descriptor.JobName, new JobRef(job.JobRef).ToString());
            }
            return new ExecutionCompletion(CompletionKind.Failed)
            {
                Reason = EventReason.ExecutionTimeout,
                ReasonMessage = "Execution exceeded the configured timeout.",
            };
        }

        // The attempt token was cancelled without a timeout: an external cancel, a stolen
        // lease, the lease deadline, or a lost handler lock. The first two leave a row this
        // worker no longer owns (the completion CAS no-ops), but the lease deadline and
        // lock-loss cancel while the row is still Executing and still leased here, so
        // a reason-less completion would land terminal Failed on a recoverable job. Submit
        // a retryable failure instead: owned rows re-arm under the failure budget, unowned
        // rows still no-op. Writing (not skipping) matters because a skipped write leaves the
        // row Executing until its lease lapses.
        if (log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.AttemptAborted(log, new JobRef(job.JobRef).ToString());
        }
        return new ExecutionCompletion(CompletionKind.Failed)
        {
            Reason = EventReason.AttemptAborted,
            ReasonMessage = "Attempt aborted: the lease or a held lock could no longer be guaranteed.",
        };
    }

    /// <summary>
    /// A failure retrying cannot change: a programming error, a failed at-most-once step that throws again on every
    /// replay, a handler DI cannot build from the same registrations, a finished body whose result could not be
    /// recorded, or a handler that went on after its own control flow. It lands terminal Failed through the same
    /// completion branch as ctx.FailAsync, leaving the retry budget untouched.
    /// </summary>
    internal static bool IsNonRetryable(Exception ex) =>
        ex
            is NotImplementedException
                or NotSupportedException
                or StepFailedException
                or HandlerActivationException
                or ResultNotRecordedException
                or SwallowedControlException;

    private ExecutionCompletion Faulted(Exception ex, bool inputDeserialized, JobDescriptor descriptor, ClaimedJob job)
    {
        var nonRetryable = IsNonRetryable(ex);
        if (!inputDeserialized && log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.InputDeserializationFailed(log, ex, descriptor.JobName, new JobRef(job.JobRef).ToString());
        }

        return new ExecutionCompletion(CompletionKind.Failed)
        {
            Reason = nonRetryable ? EventReason.NonRetryableException : EventReason.UnhandledException,
            ReasonMessage = (inputDeserialized ? ex.Message : $"Input deserialization failed ({ex.GetType().Name}): {ex.Message}").Truncate(
                ActaTextLimits.EventMessage
            ),
            HandlerStatusCode = nonRetryable ? JobStatusCode.Failed : null,
            HandlerException = inputDeserialized ? ex : null,
        };
    }

    private object DeserializeInput(JobDescriptor descriptor, ClaimedJob job)
    {
        if (job.InputFormatId == 0)
        {
            // No-payload descriptors ignore both arguments; NullJobPayloadSerializer keeps the signature uniform.
            return descriptor.DeserializeInput(NullJobPayloadSerializer.Instance, default);
        }

        var serializer = serializers.Resolve(job.InputFormatId);
        var payload = JobPayload.Trusted(serializer.Format, job.Input.ToArray());
        return descriptor.DeserializeInput(serializer, payload);
    }
}
