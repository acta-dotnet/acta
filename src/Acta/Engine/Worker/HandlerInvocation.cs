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
    ILogger log
)
{
    /// <summary>
    /// The attempt's completion, or null when worker shutdown interrupted the handler and the row stays Executing
    /// for recovery.
    /// </summary>
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
            completion = Succeeded(descriptor, job, await InvokeHandlerAsync(attemptServices, descriptor, requestObject, jobContext));
        }
        catch (JobControlException control)
        {
            completion = Controlled(control, descriptor, job);
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
                log.LogInformation("WorkerRuntime: job {JobId} interrupted by worker shutdown; left Executing for recovery.", job.JobId);
                return null;
            }

            completion = Cancelled(descriptor, job, jobContext);
        }
        catch (StepOwnershipLostException ownershipLost)
        {
            // A step completion CAS matched no row: a concurrent execution re-claimed this job, so this
            // attempt is a zombie (StepOwnershipLostException says why the failure is retryable).
            completion = new ExecutionCompletion(CompletionKind.Failed)
            {
                Reason = EventReason.AttemptAborted,
                ReasonMessage = $"Step '{ownershipLost.StepName}' ownership could not be confirmed; attempt aborted.".Truncate(
                    ActaTextLimits.EventMessage
                ),
            };
            log.LogInformation(
                "WorkerRuntime: job {JobId} lost ownership of step ({Detail}) mid-flight; aborting the attempt cooperatively.",
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
            completion = ExecutionCompletion.Terminal(
                CompletionKind.Failed,
                EventReason.StepInterrupted,
                interrupted.Message,
                JobStatusCode.Failed
            );
            log.LogWarning(
                "Handler for job ({JobName}) id {JobId} did not handle StepInterruptedException for step ({Detail}); failing terminally.",
                descriptor.JobName,
                job.JobId,
                interrupted.StepName
            );
        }
        catch (Exception ex)
        {
            completion = Faulted(ex, inputDeserialized, descriptor, job);
        }

        return completion with
        {
            DurationMs = (int)Math.Min((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, int.MaxValue),
        };
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

    private ExecutionCompletion Succeeded(JobDescriptor descriptor, ClaimedJob job, JobHandlerOutput invocation)
    {
        var succeeded = new ExecutionCompletion(CompletionKind.Succeeded);
        if (!invocation.HasResult)
        {
            return succeeded;
        }

        // Null contract: a payload value is never null. A handler returning null from Task<T> is a
        // bug, surfaced loudly as Failed; "no result" is expressed by a Task/void handler, not by
        // returning null. Thrown inside the invocation's try so it flows through the normal handler-fault path.
        if (invocation.Result is null)
        {
            throw new InvalidOperationException(
                "Handler returned null for a non-null result type. Acta results cannot be null: "
                    + "use Task for no result, or wrap optional data in a non-null object."
            );
        }

        if (descriptor.OutputPayloadFormat is not { } outFormat || descriptor.SerializeOutput is not { } serializeOutput)
        {
            return succeeded;
        }

        var payload = serializeOutput(serializers.Resolve(outFormat.Id), invocation.Result);
        if (payload.Data.Length <= maxInlinePayloadBytes)
        {
            return succeeded with { ResultFormatId = payload.Format.Id, Result = payload.Data };
        }

        // The job succeeded, so it is not failed over the size of what it returned. But the
        // cap is a cap: the body is dropped rather than persisted, leaving the row in the
        // existing "no result" shape (format 0, NULL) with job.result-oversized recording
        // why. A typed read of the missing result throws rather than handing back a default.
        log.LogWarning(
            "Handler result for job ({JobName}) id {JobId} is {Count} bytes, exceeding the ({Detail}); the result body was dropped.",
            descriptor.JobName,
            job.JobId,
            payload.Data.Length,
            $"MaxInlinePayloadBytes cap of {maxInlinePayloadBytes} bytes"
        );
        return succeeded with { Reason = EventReason.ResultOversized };
    }

    /// <summary>
    /// A deliberate control signal: reschedule or durable sleep. Caught before cancellation /
    /// generic failure so it is never mis-recorded as Failed. An unknown subclass never falls
    /// into the re-arm path: it is failed like any other exception the handler let escape.
    /// </summary>
    private ExecutionCompletion Controlled(JobControlException control, JobDescriptor descriptor, ClaimedJob job)
    {
        switch (control)
        {
            case RescheduleJobException reschedule:
                return new ExecutionCompletion(CompletionKind.Rescheduled)
                {
                    Reason = EventReason.HandlerRescheduled,
                    ReasonMessage = reschedule.Message.Truncate(ActaTextLimits.EventMessage),
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
            case StepRetrySignal stepRetry:
                // An inline step still awaits its retry instant on replay, or failed in budget and scheduled a
                // retry. Re-arm budget-neutral via the existing Rescheduled path (failure_count
                // untouched) at the step's next_retry_at_utc; only the reason differs.
                return new ExecutionCompletion(CompletionKind.Rescheduled)
                {
                    Reason = EventReason.StepRetryScheduled,
                    ReasonMessage = (stepRetry.ReasonMessage ?? $"Step '{stepRetry.StepName}' retry scheduled.").Truncate(
                        ActaTextLimits.EventMessage
                    ),
                    RescheduleResumeAtUtc = stepRetry.ResumeAtUtc,
                };
            case SignalSuspendSignal signalSuspend:
                // Signal wait with no Set slot: re-arm as Suspended (no due time). complete_executions
                // locks the named slot and lands Ready instead if a raise won the race.
                return new ExecutionCompletion(CompletionKind.Suspended)
                {
                    Reason = EventReason.HandlerSuspended,
                    ReasonMessage = (signalSuspend.ReasonMessage ?? $"Signal '{signalSuspend.SignalName}' pending.").Truncate(
                        ActaTextLimits.EventMessage
                    ),
                    WaitSignalName = signalSuspend.SignalName,
                };
            case WaitTimeoutSignal waitTimeout:
                // A bounded wait outlived the expiration stored on its slot and the handler used a
                // non-Try overload. Terminate Cancelled like the Strict-deadline path: no
                // FailureCount is submitted, so complete_executions' COALESCE leaves the budget
                // untouched, and the terminal landing stamps retention so the job stops being
                // an immortal Suspended row.
                return ExecutionCompletion.Terminal(
                    CompletionKind.Cancelled,
                    EventReason.WaitTimedOut,
                    $"Durable wait '{waitTimeout.WaitName}' timed out.",
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
                // A subclass the runtime does not know: the base type is public, so user code can
                // derive one. Rethrowing it would leave the row Executing until its lease lapsed,
                // so it lands as a handler exception instead: a retried Failed, charged to the
                // budget, with the type in the reason.
                log.LogWarning(
                    control,
                    "Handler for job ({JobName}) id {JobId} threw a control signal the runtime does not recognize; transitioning to Failed.",
                    descriptor.JobName,
                    job.JobId
                );
                return new ExecutionCompletion(CompletionKind.Failed)
                {
                    Reason = EventReason.UnhandledException,
                    ReasonMessage = $"Unrecognized control signal {control.GetType().Name}: {control.Message}".Truncate(
                        ActaTextLimits.EventMessage
                    ),
                };
        }
    }

    private ExecutionCompletion Cancelled(JobDescriptor descriptor, ClaimedJob job, RuntimeJobContext jobContext)
    {
        if (jobContext.AttemptTimedOut)
        {
            // The per-attempt execution timeout fired. Record it distinctly and let the retry budget
            // decide re-arm vs terminal; a timeout is a retryable failure like an exception.
            log.LogWarning("Handler for job ({JobName}) id {JobId} exceeded its execution timeout.", descriptor.JobName, job.JobId);
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
        log.LogWarning("WorkerRuntime: job {JobId} attempt aborted mid-flight; handler stopped cooperatively.", job.JobId);
        return new ExecutionCompletion(CompletionKind.Failed)
        {
            Reason = EventReason.AttemptAborted,
            ReasonMessage = "Attempt aborted: the lease or a held lock could no longer be guaranteed.",
        };
    }

    private ExecutionCompletion Faulted(Exception ex, bool inputDeserialized, JobDescriptor descriptor, ClaimedJob job)
    {
        if (inputDeserialized)
        {
            log.LogWarning(ex, "Handler for job ({JobName}) id {JobId} threw; transitioning to Failed.", descriptor.JobName, job.JobId);
        }
        else
        {
            log.LogWarning(
                ex,
                "Input deserialization for job ({JobName}) id {JobId} failed; transitioning the attempt to Failed.",
                descriptor.JobName,
                job.JobId
            );
        }

        // A failure retrying cannot change, a programming error or a step that already spent its budget and so throws
        // again on every replay, lands terminal Failed through the same completion branch as ctx.FailAsync, leaving the
        // retry budget untouched.
        var nonRetryable = ex is NotImplementedException or NotSupportedException or StepExhaustedException;
        return new ExecutionCompletion(CompletionKind.Failed)
        {
            Reason = nonRetryable ? EventReason.NonRetryableException : EventReason.UnhandledException,
            ReasonMessage = (inputDeserialized ? ex.Message : $"Input deserialization failed ({ex.GetType().Name}): {ex.Message}").Truncate(
                ActaTextLimits.EventMessage
            ),
            HandlerStatusCode = nonRetryable ? JobStatusCode.Failed : null,
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
        var payload = JobPayload.FromBytes(serializer.Format, job.Input.ToArray());
        return descriptor.DeserializeInput(serializer, payload);
    }
}
