#nullable enable

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Acta;

internal sealed partial class RuntimeJobContext
{
    private protected override Task RunStepCoreAsync(
        string name,
        Func<CancellationToken, Task> body,
        StepOptions options,
        Func<CancellationToken, Task<bool>>? check,
        CancellationToken ct
    ) =>
        RunStepImplAsync<bool>(
            name,
            [DebuggerDisableUserUnhandledExceptions]
            async (token) =>
            {
                await body(token);
                return false;
            },
            storeResult: false,
            options,
            check is null ? null : async token => (await check(token), false),
            ct
        );

    private protected override Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        Func<CancellationToken, Task<(bool Found, TResult Value)>>? check,
        CancellationToken ct
    ) => RunStepImplAsync(name, body, storeResult: true, options, check, ct);

    /// <summary>
    /// Durable step orchestration: start (invoke, replay, failed, or interrupted), ask the check of a step started before,
    /// run the body unless the check found its effect, record the success. An ordinary body's failure, or a check's,
    /// propagates and fails the attempt, so the job's retry runs it again; an at-most-once body's failure is recorded
    /// terminal first, unless its outcome is unknown. A debugger stops on the failure once, where the attempt ends.
    /// </summary>
    [DebuggerDisableUserUnhandledExceptions]
    private async Task<TResult> RunStepImplAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        bool storeResult,
        StepOptions options,
        Func<CancellationToken, Task<(bool Found, TResult Value)>>? check,
        CancellationToken ct
    )
    {
        var start = await FencedAsync(() => _executionStore.StartJobStepAsync(Fence, name, options.AtMostOnce, ct));
        switch (start.Outcome)
        {
            case StartJobStepOutcomeCode.ReplaySuccess:
                RecordStep(name, "reused");
                return storeResult ? DeserializeStepResult<TResult>(name, start.ResultFormatId, start.Result) : default!;
            case StartJobStepOutcomeCode.Failed:
                throw new StepFailedException(name, start.FailureMessage);
            case StartJobStepOutcomeCode.Interrupted:
                // An AtMostOnce step re-entered before its outcome was recorded: the row was terminalized
                // Interrupted by start_job_step. Do NOT run the body; the outcome is ambiguous (ran 0 or 1
                // times) and the handler must reconcile externally.
                RecordStep(name, "interrupted");
                throw new StepInterruptedException(name);
            case StartJobStepOutcomeCode.Invoke:
            case StartJobStepOutcomeCode.InvokeAgain:
                break;
            default:
                throw new InvalidOperationException($"start_job_step returned an unknown outcome for job {JobId}, step '{name}'.");
        }

        TResult value;
        (bool Found, TResult Value) found = default;
        try
        {
            // Only a step started before can have had its effect without a record, so only then is the outside world asked.
            if (check is not null && start.Outcome == StartJobStepOutcomeCode.InvokeAgain)
            {
                found = await check(ct);
            }

            value = found.Found ? found.Value : await body(ct);
        }
        catch (JobControlException control) when (options.AtMostOnce && control is not (HandlerFailException or HandlerCancelException))
        {
            // A wait begun outside the body and awaited inside it got past the refusal at the call; resuming it would
            // find the step started and interrupt it, so it is refused here. Fail and cancel end the job and pass
            // through.
            throw await RecordFailureAsync(name, start.Version, AtMostOnceSuspensionRefused($"A {control.GetType().Name}", name), ct);
        }
        catch (JobControlException control) when (check is not null && control is not (HandlerFailException or HandlerCancelException))
        {
            // The same carried wait in a check step: its resume would ask the check, which can find the body's effect and
            // skip the rest of the body. The refusal fails the job at once, the step left as it is.
            RecordStep(name, "failed");
            throw CheckStepSuspensionRefused($"A {control.GetType().Name}", name);
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

            // A body that cannot tell whether its effect happened, itself or through a step inside it, ends the step as a
            // crash does: left started, so no retry or restart runs the body again.
            if (ex is OutcomeUnknownException or StepInterruptedException)
            {
                RecordStep(name, "interrupted");
                throw new StepInterruptedException(name, ex);
            }

            throw await RecordFailureAsync(name, start.Version, ex, ct);
        }

        // The body has run, or the check found its effect, and a null result, or one whose JSON is null, stores none. A result
        // that cannot be recorded fails the step for good rather than the attempt, whose retry would run the body again. The
        // step is left started, so an at-most-once body is not run by a restart either.
        JobPayload payload;
        try
        {
            payload = !storeResult || value is null ? JobPayload.None : JsonSerializer().Serialize(value);
        }
        catch (Exception ex)
            when (ex is not (JobControlException or StepFailedException)
                && (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            )
        {
            throw Unrecorded($"it could not be serialized: {ex.Message.TrimEnd('.')}", ex);
        }

        if (payload.IsJsonNull)
        {
            payload = JobPayload.None;
        }

        var size = payload.Data.Length;
        var ceiling = JobsOptions.ResultCeilingBytes(_maxInlinePayloadBytes);
        if (size > ceiling)
        {
            throw Unrecorded($"it is {size} bytes, over the {ceiling}-byte ceiling on a stored result", cause: null);
        }

        if (size > _maxInlinePayloadBytes && _log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.ResultOverCap(_log, JobName, JobRef.ToString(), $"step '{name}': {size} bytes, cap {_maxInlinePayloadBytes} bytes");
        }

        var success = await CompleteJobStepAsync(
            new CompleteJobStepCommand(
                Fence,
                name,
                Succeeded: true,
                payload.Format.Id,
                payload.IsNone ? null : payload.Data.ToArray(),
                FailureMessage: null,
                start.Version
            ),
            ct
        );

        if (success.Outcome == CompleteJobStepOutcomeCode.StaleVersion)
        {
            throw new StepOwnershipLostException(name);
        }

        RecordStep(name, found.Found ? "found" : "succeeded");
        return value;

        StepFailedException Unrecorded(string why, Exception? cause)
        {
            RecordStep(name, "failed");
            return new StepFailedException(
                name,
                found.Found
                    ? $"its check found its effect, and the value found was not recorded: {why}"
                    : $"its body ran, and its result was not recorded: {why}",
                cause
            );
        }
    }

    private Task<CompleteJobStepResponse> CompleteJobStepAsync(CompleteJobStepCommand command, CancellationToken ct) =>
        FencedAsync(() => _executionStore.CompleteJobStepAsync(command, ct));

    /// <summary>Records an at-most-once body's failure terminal and answers the exception its call throws.</summary>
    private async Task<StepFailedException> RecordFailureAsync(string name, int version, Exception cause, CancellationToken ct)
    {
        var message = cause.FailureText().Truncate(ActaTextLimits.StepFailureMessage);
        var failure = await CompleteJobStepAsync(
            new CompleteJobStepCommand(Fence, name, Succeeded: false, ResultFormatId: 0, Result: null, message, version),
            ct
        );

        if (failure.Outcome == CompleteJobStepOutcomeCode.StaleVersion)
        {
            throw new StepOwnershipLostException(name);
        }

        RecordStep(name, "failed");
        if (_log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.StepFailed(_log, JobName, JobRef.ToString(), $"step '{name}': {message}");
        }
        return new StepFailedException(name, message, cause);
    }

    /// <summary>
    /// A replayed step's stored result; a serializer that cannot read it throws to the attempt as a contract mismatch. A step
    /// that returned null stored none and replays as null, which a non-nullable value type cannot hold.
    /// </summary>
    [DebuggerDisableUserUnhandledExceptions]
    private TResult DeserializeStepResult<TResult>(string name, byte resultFormatId, byte[]? result)
    {
        try
        {
            if (result is null)
            {
                return default(TResult) is null ? default! : throw new InvalidOperationException($"Step '{name}' stored no result.");
            }

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
