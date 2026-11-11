#nullable enable

using Microsoft.Extensions.Logging;

namespace Acta;

internal sealed partial class RuntimeJobContext
{
    private protected override Task RunStepCoreAsync(
        string name,
        Func<CancellationToken, Task> body,
        StepOptions options,
        CancellationToken ct
    ) =>
        RunStepImplAsync<bool>(
            name,
            async token =>
            {
                await body(token);
                return false;
            },
            storeResult: false,
            options,
            ct
        );

    private protected override Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        CancellationToken ct
    ) => RunStepImplAsync(name, body, storeResult: true, options, ct);

    /// <summary>
    /// Durable step orchestration: start (invoke, replay, exhausted, or interrupted), run the body, record the success.
    /// An ordinary body's failure propagates and fails the attempt, so the job's retry runs it again; an at-most-once
    /// body's failure is recorded terminal first.
    /// </summary>
    private async Task<TResult> RunStepImplAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        bool storeResult,
        StepOptions options,
        CancellationToken ct
    )
    {
        var start = await FencedAsync(() => _executionStore.StartJobStepAsync(Fence, name, options.AtMostOnce, ct));
        switch (start.Outcome)
        {
            case StartJobStepOutcomeCode.ReplaySuccess:
                RecordStep(name, "replayed");
                return storeResult ? DeserializeStepResult<TResult>(name, start.ResultFormatId, start.Result!) : default!;
            case StartJobStepOutcomeCode.Exhausted:
                throw new StepExhaustedException(name, start.FailureCode, start.FailureMessage);
            case StartJobStepOutcomeCode.Interrupted:
                // An AtMostOnce step re-entered before its outcome was recorded: the row was terminalized
                // Interrupted by start_job_step. Do NOT run the body; the outcome is ambiguous (ran 0 or 1
                // times) and the handler must reconcile externally.
                RecordStep(name, "interrupted");
                throw new StepInterruptedException(name);
            case StartJobStepOutcomeCode.Invoke:
                break;
            default:
                throw new InvalidOperationException($"start_job_step returned an unknown outcome for job {JobId}, step '{name}'.");
        }

        TResult value;
        try
        {
            value = await body(ct);
        }
        catch (JobControlException control) when (options.AtMostOnce && control is not (HandlerFailException or HandlerCancelException))
        {
            // A suspension the body threw itself rather than through a guarded call: its resume would find the step
            // started and interrupt it, so it is refused as those calls are. Fail and cancel end the job and pass through.
            throw await ExhaustAsync(
                name,
                start.Version,
                AtMostOnceSuspensionRefused($"A thrown {control.GetType().Name}", name).Message,
                ct
            );
        }
        catch (Exception ex)
            when (ex is not (JobControlException or ExecutionFencedException)
                && (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            )
        {
            // A business failure. A genuine parent or caller cancel is NOT caught here, nor a control verb the body
            // called (fail, cancel, reschedule, a wait), nor a write the fence refused: each decides the attempt's
            // outcome, not this step's.
            if (!options.AtMostOnce)
            {
                RecordStep(name, "failed");
                throw;
            }

            throw await ExhaustAsync(name, start.Version, ex.Message, ct);
        }

        // The body has run. A result that cannot be recorded fails the step for good rather than the attempt, whose retry
        // would run the body again. The step is left started, so an at-most-once body is not run by a restart either.
        JobPayload payload;
        try
        {
            payload =
                !storeResult ? JobPayload.None
                : value is null ? throw Unrecorded("it returned null for a non-null result type", cause: null)
                : SerializeStepResult(value);
        }
        catch (PayloadTooLargeException ex)
        {
            throw Unrecorded(ex.Message.TrimEnd('.'), ex);
        }
        catch (Exception ex)
            when (ex is not (JobControlException or StepExhaustedException)
                && (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            )
        {
            throw Unrecorded($"it could not be serialized: {ex.Message.TrimEnd('.')}", ex);
        }

        var success = await CompleteJobStepAsync(
            new CompleteJobStepCommand(
                Fence,
                name,
                Succeeded: true,
                payload.Format.Id,
                storeResult ? payload.Data.ToArray() : null,
                FailureCode: null,
                FailureMessage: null,
                start.Version
            ),
            ct
        );

        if (success.Outcome == CompleteJobStepOutcomeCode.StaleVersion)
        {
            throw new StepOwnershipLostException(name);
        }

        RecordStep(name, "succeeded");
        return value;

        StepExhaustedException Unrecorded(string why, Exception? cause)
        {
            RecordStep(name, "failed");
            return new StepExhaustedException(name, null, $"its body ran, and its result was not recorded: {why}", cause);
        }
    }

    private Task<CompleteJobStepResponse> CompleteJobStepAsync(CompleteJobStepCommand command, CancellationToken ct) =>
        FencedAsync(() => _executionStore.CompleteJobStepAsync(command, ct));

    /// <summary>Records an at-most-once body's failure terminal and answers the exception its call throws.</summary>
    private async Task<StepExhaustedException> ExhaustAsync(string name, int version, string failureMessage, CancellationToken ct)
    {
        var message = failureMessage.Truncate(ActaTextLimits.StepFailureMessage);
        var failure = await CompleteJobStepAsync(
            new CompleteJobStepCommand(
                Fence,
                name,
                Succeeded: false,
                ResultFormatId: 0,
                Result: null,
                StepFailureCode.UnhandledException,
                message,
                version
            ),
            ct
        );

        if (failure.Outcome == CompleteJobStepOutcomeCode.StaleVersion)
        {
            throw new StepOwnershipLostException(name);
        }

        RecordStep(name, "exhausted");
        if (_log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.StepExhausted(_log, JobId, $"step '{name}': {message}");
        }
        return new StepExhaustedException(name, StepFailureCode.UnhandledException, message);
    }

    private JobPayload SerializeStepResult<TResult>(TResult value)
    {
        var payload = JsonSerializer().Serialize(value);
        EnsureInlineSize("step result", payload);
        return payload;
    }

    private TResult DeserializeStepResult<TResult>(string name, byte resultFormatId, byte[] result)
    {
        try
        {
            var serializer = _serializers.Resolve(resultFormatId);
            var payload = JobPayload.Trusted(serializer.Format, result);
            return serializer.Deserialize<TResult>(payload)!;
        }
        catch (Exception ex) when (ex is not StepResultContractMismatchException)
        {
            throw new StepResultContractMismatchException(name, typeof(TResult), ex);
        }
    }

    private void RecordStep(string name, string outcome)
    {
        // step_name is deliberately NOT a metric tag (user-defined means unbounded cardinality); it lives
        // on the log line only. The counter is tagged (namespace, job_name, outcome).
        _ = name;
        _metrics?.RecordStep(JobNamespace, JobName, outcome);
    }
}
