#nullable enable

using System.Globalization;
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
    /// Durable step orchestration: start (decide invoke/replay/suspend/exhausted), run the body,
    /// complete (success or retry/exhaust). Retries re-arm the parent budget-neutrally via
    /// StepRetrySignal; the policy is resolved live from the parent [Job] defaults + per-step
    /// overrides each attempt and never persisted.
    /// </summary>
    private async Task<TResult> RunStepImplAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        bool storeResult,
        StepOptions options,
        CancellationToken ct
    )
    {
        // AtMostOnce forces a single body invocation: the parent [Job] MaxAttempts default must not leak
        // in and re-run the body. Any conflicting override was already rejected by StepOptionsBuilder.Build.
        var maxAttempts = options.AtMostOnce ? 1 : options.MaxAttempts ?? _stepRetryDefaults.MaxAttempts;
        var retryWindowSeconds = options.AtMostOnce ? (int?)null : options.RetryWindowSeconds;

        var start = await _executionStore.StartStepAsync(JobId, name, options.AtMostOnce, ct);
        switch (start.Outcome)
        {
            case StartStepOutcomeCode.ReplaySuccess:
                RecordStep(name, "replayed");
                return storeResult ? DeserializeStepResult<TResult>(name, start.ResultFormatId, start.Result!) : default!;
            case StartStepOutcomeCode.Exhausted:
                throw new StepExhaustedException(name, start.AttemptNumber, start.ReasonCode, start.ReasonMessage);
            case StartStepOutcomeCode.Interrupted:
                // An AtMostOnce step re-entered before its outcome was recorded: the row was terminalized
                // Interrupted by start_step. Do NOT run the body; the outcome is ambiguous (ran 0 or 1
                // times) and the handler must reconcile externally.
                RecordStep(name, "interrupted");
                throw new StepInterruptedException(name);
            case StartStepOutcomeCode.Suspend:
                throw new StepRetrySignal(start.NextRetryAtUtc!.Value, name, reasonMessage: null);
            case StartStepOutcomeCode.Invoke:
                break;
            default:
                throw new InvalidOperationException($"start_step returned an unknown outcome for job {JobId}, step '{name}'.");
        }

        TResult value;
        try
        {
            value = await body(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A business failure: record it and let the routine decide retry-vs-exhaust. A genuine parent
            // or caller cancel is NOT caught here: it propagates so HandlerInvocation/caller cancellation handles it,
            // never recording a step failure.
            var delaySeconds = BackoffSchedule.ComputeDelaySeconds(
                start.AttemptNumber,
                options.BackoffInitialDelaySeconds ?? _stepRetryDefaults.BackoffInitialDelaySeconds,
                options.BackoffMaxDelaySeconds ?? _stepRetryDefaults.BackoffMaxDelaySeconds,
                options.BackoffMultiplier ?? _stepRetryDefaults.BackoffMultiplier,
                options.BackoffJitter ?? _stepRetryDefaults.BackoffJitter
            );
            var message = ex.Message.Truncate(ActaTextLimits.ReasonMessage);

            var failure = await _executionStore.CompleteStepAsync(
                new CompleteStepCommand(
                    JobId,
                    name,
                    Succeeded: false,
                    ResultFormatId: 0,
                    Result: null,
                    EventReasonCode.JobUnhandledException,
                    message,
                    delaySeconds,
                    maxAttempts,
                    retryWindowSeconds,
                    start.Version
                ),
                ct
            );

            if (failure.Outcome == CompleteStepOutcomeCode.StaleVersion)
            {
                throw new StepOwnershipLostException(name);
            }

            if (failure.Outcome == CompleteStepOutcomeCode.Exhausted)
            {
                RecordStep(name, "exhausted");
                _log.LogWarning(
                    "Step ({Operation}) on job {JobId} exhausted after attempt {Count}: ({Detail})",
                    name,
                    JobId,
                    start.AttemptNumber,
                    message
                );
                throw new StepExhaustedException(name, start.AttemptNumber, EventReasonCode.JobUnhandledException, message);
            }

            RecordStep(name, "failed");
            _log.LogInformation(
                "Step ({Operation}) on job {JobId} failed on attempt {Count}; retry at ({Detail}).",
                name,
                JobId,
                start.AttemptNumber,
                failure.NextRetryAtUtc?.ToString("o", CultureInfo.InvariantCulture)
            );
            throw new StepRetrySignal(failure.NextRetryAtUtc!.Value, name, message);
        }

        if (storeResult && value is null)
        {
            // Null contract: an step result is never null (same rule as a handler Task<T> result).
            throw new InvalidOperationException(
                $"Step '{name}' returned null for a non-null result type. Acta results cannot be null: "
                    + "use the non-result RunStepAsync overload, or wrap optional data in a non-null object."
            );
        }

        var payload = storeResult ? SerializeStepResult(value!) : JobPayload.None;
        var success = await _executionStore.CompleteStepAsync(
            new CompleteStepCommand(
                JobId,
                name,
                Succeeded: true,
                payload.Format.Id,
                storeResult ? payload.Data.ToArray() : null,
                ReasonCode: null,
                ReasonMessage: null,
                DelaySeconds: 0,
                maxAttempts,
                retryWindowSeconds,
                start.Version
            ),
            ct
        );

        if (success.Outcome == CompleteStepOutcomeCode.StaleVersion)
        {
            throw new StepOwnershipLostException(name);
        }

        RecordStep(name, "succeeded");
        return value;
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
            var payload = JobPayload.FromBytes(serializer.Format, result);
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
