#nullable enable

using System.Diagnostics;
using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Modules.Execution.Timers;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Locks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// Per-attempt <see cref="JobContext"/> supplied to handlers. Inspection fields come from the
/// claimed row; progress, variable, and sleep operations delegate to the per-routine slice handlers.
/// </summary>
internal sealed class RuntimeJobContext(
    ClaimedJob job,
    string jobName,
    string namespaceName,
    int namespaceId,
    int leaseTtlSeconds,
    IJobStore jobStore,
    Acta.Runtime.Modules.Execution.Signals.ISignalStore signalStore,
    IAlertSink alerts,
    Acta.Runtime.Modules.Execution.IExecutionStore executionStore,
    IJobPayloadSerializerRegistry serializers,
    ILockStore lockStore,
    IReadOnlyList<string> triggeringScheduleNames,
    DateTime? deadlineAtUtc,
    CancellationToken cancellationToken,
    int maxInlinePayloadBytes = int.MaxValue,
    RunningAttempt? runningAttempt = null,
    StepRetryDefaults stepRetryDefaults = default,
    ILogger? log = null,
    JobMetrics? metrics = null,
    IJobs? jobs = null,
    string? tenantKey = null,
    int workerId = 0,
    WorkerWakeupPublisher? wakeupPublisher = null,
    Acta.Runtime.Services.Time.IActaClock? clock = null
) : JobContext
{
    private const string ProgressVariableName = "sys.progress";

    private readonly IJobStore _jobStore = jobStore;
    private readonly Acta.Runtime.Modules.Execution.Signals.ISignalStore _signalStore = signalStore;
    private readonly IAlertSink _alerts = alerts;
    private readonly Acta.Runtime.Modules.Execution.IExecutionStore _executionStore = executionStore;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly ILockStore _lockStore = lockStore;
    private readonly int _namespaceId = namespaceId;
    private readonly int _leaseTtlSeconds = leaseTtlSeconds;
    private readonly int _maxInlinePayloadBytes = maxInlinePayloadBytes;
    private readonly RunningAttempt? _runningAttempt = runningAttempt;
    private readonly StepRetryDefaults _stepRetryDefaults = stepRetryDefaults;
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly JobMetrics? _metrics = metrics;
    private readonly IJobs? _jobs = jobs;
    private readonly WorkerWakeupPublisher? _wakeupPublisher = wakeupPublisher;
    private readonly Acta.Runtime.Services.Time.IActaClock? _clock = clock;

    /// <summary>
    /// Whether this attempt's cancellation was the execution-timeout firing rather than an external
    /// cancel, read by <see cref="JobExecution"/> to record the timeout reason and apply the retry budget.
    /// </summary>
    internal bool AttemptTimedOut => _runningAttempt?.TimedOut ?? false;

    public override long JobId { get; } = job.JobId;
    public override string JobNamespace { get; } = namespaceName;
    public override int NamespaceId => _namespaceId;
    public override int? TenantId { get; } = job.TenantId;
    public override string? TenantKey { get; } = tenantKey;
    public override string JobName { get; } = jobName;
    public override JobRef JobRef { get; } = new JobRef(job.JobRef);
    public override int ExecutionNumber { get; } = job.ExecutionNumber;
    public override int WorkerId { get; } = workerId;
    public override CancellationToken CancellationToken { get; } = cancellationToken;
    public override IReadOnlyList<string> TriggeringScheduleNames { get; } = triggeringScheduleNames;
    public override DateTime? DeadlineAtUtc { get; } = deadlineAtUtc;

    protected override Task SetProgressCoreAsync<T>(T value, CancellationToken ct)
    {
        var payload = JsonSerializer().Serialize(value);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize("progress", payload);
        return CheckpointSlot.SetAsync(_executionStore, JobId, JobCheckpointKindCode.Progress, ProgressVariableName, payload, ct);
    }

    protected override Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct)
    {
        var payload = JsonSerializer().Serialize(value);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize($"variable '{name}'", payload);
        return CheckpointSlot.SetAsync(_executionStore, JobId, JobCheckpointKindCode.Variable, name, payload, ct);
    }

    protected override Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct)
    {
        ValidatePayloadFormat(payload);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize($"variable '{name}'", payload);
        return CheckpointSlot.SetAsync(_executionStore, JobId, JobCheckpointKindCode.Variable, name, payload, ct);
    }

    protected override async Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct)
        where T : default
    {
        var value = await CheckpointSlot.GetAsync(_executionStore, JobId, JobCheckpointKindCode.Variable, name, ct);
        return value is null ? (false, default) : (true, Deserialize<T>(value));
    }

    protected override async Task<T> GetOrSetVariableCoreAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct
    )
    {
        var existing = await CheckpointSlot.GetAsync(_executionStore, JobId, JobCheckpointKindCode.Variable, name, ct);
        if (existing is not null)
        {
            return Deserialize<T>(existing);
        }

        var value =
            await valueFactory(ct)
            ?? throw new InvalidOperationException("Variable factory returned null. Use DeleteVariableAsync to clear a variable.");
        var payload = JsonSerializer().Serialize(value);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize($"variable '{name}'", payload);
        var stored = await CheckpointSlot.GetOrSetAsync(_executionStore, JobId, JobCheckpointKindCode.Variable, name, payload, ct);
        return Deserialize<T>(stored);
    }

    // One deadline for a whole group wait, stored under a reserved sys. slot name so it can never
    // collide with a user variable. Written once and read forever after: the get-or-set upsert returns
    // whatever landed first, so a replay, a crash, or a second call with a different timeout all resolve
    // to the same instant and the group's budget can never restart. Ticks rather than a serialized
    // DateTime, so the stored bytes are exact and carry no format or kind ambiguity across a round trip.
    protected override async Task<WaitDeadline> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        // The DB clock, not the host's: the slot dues this deadline is spent against are stamped by the
        // database, so measuring the group against anything else would import the worker's skew.
        var nowUtc = await Clock().GetUtcNowAsync(ct);
        var stored = await CheckpointSlot.GetOrSetAsync(
            _executionStore,
            JobId,
            JobCheckpointKindCode.Variable,
            name,
            JsonSerializer().Serialize((nowUtc + timeout).Ticks),
            ct
        );
        return new WaitDeadline(new DateTime(Deserialize<long>(stored), DateTimeKind.Utc), nowUtc);
    }

    private Acta.Runtime.Services.Time.IActaClock Clock() =>
        _clock ?? throw new InvalidOperationException("Bounded group waits need the Acta clock; this context was built without one.");

    protected override Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct) =>
        CheckpointSlot.ExistsAsync(_executionStore, JobId, JobCheckpointKindCode.Variable, name, ct);

    protected override Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct) =>
        CheckpointSlot.DeleteAsync(_executionStore, JobId, JobCheckpointKindCode.Variable, name, ct);

    protected override Task ResetStateCoreAsync(CancellationToken ct) => _jobStore.ResetJobStateAsync(JobId, ct);

    protected override async Task SleepCoreAsync(string name, TimeSpan? delay, DateTime? resumeAtUtc, string? reason, CancellationToken ct)
    {
        var delaySeconds = delay is { } d ? (int)d.TotalSeconds : (int?)null;
        var decision = await _executionStore.ArmOrConsumeSleepTimerAsync(
            new Acta.Runtime.Modules.Execution.ArmOrConsumeSleepTimerCommand(JobId, name, delaySeconds, resumeAtUtc),
            ct
        );
        switch (decision.Outcome)
        {
            case SleepOutcome.Continue:
                return;
            case SleepOutcome.Suspend:
                // The host writes the stored due to runtimes.next_run_at_utc and finalizes the attempt Suspended.
                throw new SuspendSignal(decision.DueAtUtc!.Value, reason);
            default:
                throw new InvalidOperationException(
                    $"Job {JobId} already has a pending sleep; only one concurrent sleep is allowed per job."
                );
        }
    }

    protected override Task<SignalWaitOutcome> WaitSignalCoreAsync(string name, CancellationToken ct) =>
        WaitSignalCoreAsync(name, timeoutSeconds: null, resumeOnTimeout: false, ct);

    // Policy is code, not state: the slot records only the absolute expiration, so which overload the
    // handler called decides what an expired wait does, and a replay of the other overload against the
    // same slot is free to decide differently.
    protected override async Task<SignalWaitOutcome> WaitSignalCoreAsync(
        string name,
        int? timeoutSeconds,
        bool resumeOnTimeout,
        CancellationToken ct
    )
    {
        var kind = name.StartsWith(RaiseChildLatch.NamePrefix, StringComparison.Ordinal)
            ? JobCheckpointKindCode.ChildLatch
            : JobCheckpointKindCode.Signal;
        var decision = await _signalStore.WaitSignalAsync(JobId, kind, name, timeoutSeconds, ct);
        return decision.Outcome switch
        {
            SignalWaitOutcomeCode.ContinueSet => new SignalWaitOutcome(decision.ValueFormatId, decision.Value),
            SignalWaitOutcomeCode.SuspendPending => throw new SignalSuspendSignal(name, reasonMessage: null), // The host locks the slot and finalizes the attempt Suspended (or Ready if a raise won the race).
            SignalWaitOutcomeCode.TimedOut when resumeOnTimeout => new SignalWaitOutcome(0, null, TimedOut: true),
            SignalWaitOutcomeCode.TimedOut => throw new WaitTimeoutSignal(name), // The host lands the attempt Cancelled, budget-neutral, like the Strict-deadline path.
            _ => throw new InvalidOperationException($"wait_signal returned an unknown outcome for job {JobId}, signal '{name}'."),
        };
    }

    // The wait timed out, so this parent stopped waiting; the child and everything under it is work
    // nobody is going to read. Reason JobWaitTimedOut rather than JobParentCancelled, because the
    // parent was NOT cancelled and the timeline must not say it was.
    //
    // Follow-up transactions, like every other cascade, and NOT atomic with the Expired flip that
    // preceded them. A crash mid-walk leaves live stragglers, and no maintenance pass sweeps them: the
    // only repair is the parent replaying this wait, re-deriving TimedOut off the Expired slot, and
    // re-running the cancel. A parent that lands terminal without replaying strands them, which
    // docs/technical/known-limitations.md states as a known limitation.
    protected override async Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct)
    {
        // Parentage is a safety rail, not an optimization. A wait on an id that is not this job's child
        // can only ever expire, because nothing will raise a latch nobody writes, so a handler bug or a
        // stale id reaches this method by construction, not by chance. Without the check it would cancel
        // an unrelated job, in another namespace or tenant, irreversibly and silently.
        var children = await _executionStore.GetChildJobIdsAsync(JobId, ct);
        if (!children.Contains(childJobId))
        {
            // The awaiting job and its namespace are already on the log scope, so the subject here is
            // the target and nothing else repeats. SubjectRef carries the awaited job's numeric id, not
            // a minted JobRef as the alert transport's does: resolving a ref would cost a store round
            // trip on a path whose whole point is to be the cheap safety rail. One warning, not a
            // throw: the wait did time out, and a handler bug should be loud in telemetry rather than
            // destructive in the ledger.
            _log.LogWarning(
                "({Operation}) ({Outcome}) for job {SubjectRef} ({Reason}); the awaited job is not a child of this one, "
                    + "so its wait could only ever expire.",
                "child-wait-timeout-cancel",
                "Skipped",
                childJobId.ToString(CultureInfo.InvariantCulture),
                "not-a-child"
            );
            return;
        }

        var input = new JobControlInput(
            new JobControlActor(ActorCode.Sys),
            JobEventReasonCode.JobWaitTimedOut,
            "Parent's child wait timed out."
        );

        var cancel = await _jobStore.CancelJobAsync(childJobId, input, ct);
        if (cancel.Outcome.Action == JobControlActionInternal.Applied)
        {
            await WakeCompletionAsync(childJobId, ct);
        }

        // The child's own latch on this job is Expired, so no raise is needed or possible here; the
        // descendants' latches sit on parents this walk has already made terminal.
        foreach (var cancelledId in await CancelDescendants.Run(_executionStore, _jobStore, childJobId, input, ct))
        {
            await WakeCompletionAsync(cancelledId, ct);
        }
    }

    private async Task WakeCompletionAsync(long jobId, CancellationToken ct)
    {
        if (_wakeupPublisher is { } publisher)
        {
            await publisher.WakeAsync(WorkerWakeupChannel.JobCompletion(jobId), WorkerWakeupReason.JobFinished, ct);
        }
    }

    protected override T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value)
        where T : default
    {
        var serializer = _serializers.Resolve(valueFormatId);
        var payload = JobPayload.FromBytes(serializer.Format, value);
        return serializer.Deserialize<T>(payload);
    }

    protected override async Task<JobEnqueueOutcome> StartChildCoreAsync<TInput>(
        TInput input,
        JobEnqueueOptions options,
        CancellationToken ct
    ) => await Jobs().EnqueueAsync(input, options, ct);

    protected override async Task<JobEnqueueOutcome> StartChildCoreAsync(JobEnqueueRequest request, CancellationToken ct) =>
        await Jobs().EnqueueAsync(request, ct);

    protected override async Task<TResult?> GetChildResultCoreAsync<TResult>(long childJobId, CancellationToken ct)
        where TResult : default => await Jobs().GetResultAsync<TResult>(JobLookup.ById(childJobId), ct);

    private IJobs Jobs() =>
        _jobs ?? throw new InvalidOperationException("Child job operations need the IJobs facade; this context was built without one.");

    protected override Task RunStepCoreAsync(string name, Func<CancellationToken, Task> body, StepOptions options, CancellationToken ct) =>
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

    protected override Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        CancellationToken ct
    ) => RunStepImplAsync(name, body, storeResult: true, options, ct);

    // Durable step orchestration: start (decide invoke/replay/suspend/exhausted), run the body,
    // complete (success or retry/exhaust). Retries re-arm the parent budget-neutrally via
    // StepRetrySignal; the policy is resolved live from the parent [Job] defaults + per-step
    // overrides each attempt and never persisted.
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
        var maxAttempts = options.AtMostOnce ? (short)1 : (short)(options.MaxAttempts ?? _stepRetryDefaults.MaxAttempts);
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
            // or caller cancel is NOT caught here: it propagates so JobExecution/caller cancellation handles it,
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
                    JobEventReasonCode.JobUnhandledException,
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
                throw new StepExhaustedException(name, start.AttemptNumber, JobEventReasonCode.JobUnhandledException, message);
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

    protected override async Task<Guid?> AcquireLockCoreAsync(string key, LockScope scope, CancellationToken ct)
    {
        // Capture the request-start BEFORE the acquire: the store stamps the lease no earlier than this
        // instant, so requestStart + TTL is a conservative lower bound on when the lock actually expires.
        var requestedAt = Stopwatch.GetTimestamp();
        var token = await _lockStore.TryAcquireAsync(ComposeLockKey(key, scope), TimeSpan.FromSeconds(_leaseTtlSeconds), JobId, ct);
        if (token is { } held)
        {
            // Register so the lock heartbeat extends this handler-acquired lock for as long as the
            // critical section runs; without it a long action would outlive the lease TTL and the lock
            // could be stolen mid-section.
            _runningAttempt?.TrackLock(held, requestedAt + LeaseTtlStopwatchTicks());
        }
        return token?.HoldToken;
    }

    private long LeaseTtlStopwatchTicks() => (long)(_leaseTtlSeconds * (double)Stopwatch.Frequency);

    protected override Task ReleaseLockCoreAsync(string key, LockScope scope, Guid holdToken, CancellationToken ct)
    {
        var token = new LockToken(ComposeLockKey(key, scope), holdToken);
        // Untrack before the store release so a heartbeat tick racing this release sees Holds()==false
        // and does not mistake the normal release for a steal.
        _runningAttempt?.UntrackLock(token);
        return _lockStore.ReleaseAsync(token, ct);
    }

    protected override void OnLockReleaseFailure(string key, LockScope scope, Exception exception) =>
        RecordLockReleaseFailure(scope == LockScope.Global ? "handler_global" : "handler_namespace", ComposeLockKey(key, scope), exception);

    private LockToken? _exclusiveKeyToken;

    // Exclusive-key admission mutex, taken by the runner after the start CAS and before the handler.
    // Key space {ns_id}.excl.{key} is disjoint from RunWithLock's {ns_id}.lock.{key} / global.lock.{key}.
    // Normalized defensively so one mutex group across case never depends on the stored value alone.
    internal async Task<bool> TryAcquireExclusiveKeyLockAsync(string exclusiveKey, CancellationToken ct)
    {
        var key = $"{_namespaceId}.excl.{IdentifierSyntax.NormalizeKey(exclusiveKey, nameof(exclusiveKey))}";
        var requestedAt = Stopwatch.GetTimestamp();
        var token = await _lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(_leaseTtlSeconds), JobId, ct);
        if (token is { } held)
        {
            _runningAttempt?.TrackLock(held, requestedAt + LeaseTtlStopwatchTicks());
            _exclusiveKeyToken = held;
        }
        return token is not null;
    }

    internal async Task ReleaseExclusiveKeyLockAsync(CancellationToken ct)
    {
        if (_exclusiveKeyToken is not { } token)
        {
            return;
        }
        _exclusiveKeyToken = null;
        _runningAttempt?.UntrackLock(token);
        try
        {
            await _lockStore.ReleaseAsync(token, ct);
        }
        catch (Exception ex)
        {
            RecordLockReleaseFailure("exclusive_key", token.Key, ex);
        }
    }

    private void RecordLockReleaseFailure(string lockKind, string key, Exception exception)
    {
        _log.LogWarning(
            exception,
            "Failed to release the ({Detail}) held for job {JobId}; continuing because the lock TTL will clean it up.",
            $"{lockKind} lock '{key}'",
            JobId
        );
        _metrics?.RecordLockReleaseFailure(JobNamespace, JobName, lockKind, exception.GetType().Name);
    }

    protected override Task WriteNoteCoreAsync<T>(string message, T? detail, CancellationToken ct)
        where T : default
    {
        JobPayload? payload = null;
        if (detail is not null)
        {
            var serialized = JsonSerializer().Serialize(detail);
            EnsureInlineSize("note detail", serialized);
            payload = serialized;
        }

        // Truncated rather than rejected: the message is prose for a human reading the timeline, and
        // failing a handler's note because its sentence ran long would be a poor trade. The detail
        // payload is the part that hard-throws.
        return _executionStore.RecordJobNoteAsync(JobId, message.Truncate(ActaTextLimits.ReasonMessage)!, payload, ct);
    }

    protected override async Task RaiseAlertCoreAsync(
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    )
    {
        // Alert policy (origin/kind, default channel, the incident-identity upsert) lives behind the
        // sink on the alerting side; execution only states the intent.
        await _alerts.RaiseManualAsync(JobNamespace, JobId, severityCode, title, message, channelName, deduplicationKey, ct);
    }

    // Caller-controlled handler writes (variables, progress) HARD-THROW past the inline cap; the write
    // never reaches storage. Handler results take a separate warn-and-persist path in JobExecution.
    private void EnsureInlineSize(string entryPoint, JobPayload payload)
    {
        var length = payload.Data.Length;
        if (length > _maxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException(entryPoint, length, _maxInlinePayloadBytes);
        }
    }

    private IJobPayloadSerializer JsonSerializer() => _serializers.Resolve(JobPayloadFormat.Json.Id);

    private T Deserialize<T>(CheckpointValue row)
    {
        var serializer = _serializers.Resolve(row.ValueFormatId);
        var payload = JobPayload.FromBytes(serializer.Format, row.Value);
        return serializer.Deserialize<T>(payload);
    }

    private void ValidatePayloadFormat(JobPayload payload)
    {
        if (payload.IsNone || payload.Format.Id == 0)
        {
            throw new ArgumentException("Variable payload cannot be None.", nameof(payload));
        }
        if (!_serializers.IsRegistered(payload.Format.Id))
        {
            throw new InvalidOperationException($"No serializer registered for JobPayloadFormat id {payload.Format.Id}.");
        }
    }

    private static void RejectTopLevelJsonNull(JobPayload payload)
    {
        if (payload.Format.Id != JobPayloadFormat.Json.Id)
        {
            return;
        }

        var span = payload.Data.Span;
        var start = 0;
        var end = span.Length - 1;
        while (start <= end && char.IsWhiteSpace((char)span[start]))
        {
            start++;
        }
        while (end >= start && char.IsWhiteSpace((char)span[end]))
        {
            end--;
        }
        if (
            end - start == 3
            && span[start] == (byte)'n'
            && span[start + 1] == (byte)'u'
            && span[start + 2] == (byte)'l'
            && span[start + 3] == (byte)'l'
        )
        {
            throw new ArgumentException("Variable payload cannot be a top-level JSON null.", nameof(payload));
        }
    }

    // RunWithLock key space: namespace-scoped {ns_id}.lock.{key}, or global.lock.{key} cross-namespace.
    // The `global` sentinel cannot equal a numeric namespace id, so a global lock never collides with a
    // namespace-scoped one.
    private string ComposeLockKey(string key, LockScope scope)
    {
        key = IdentifierSyntax.NormalizeKey(key, nameof(key));
        return scope == LockScope.Global ? $"global.lock.{key}" : $"{_namespaceId}.lock.{key}";
    }
}

/// <summary>
/// The parent Job's resolved retry policy, supplied to <see cref="RuntimeJobContext"/> so an inline
/// step can resolve its effective policy live each attempt (parent defaults + per-step
/// <c>configure</c> overrides). Never persisted; re-resolved on every replay.
/// </summary>
internal readonly record struct StepRetryDefaults(
    short MaxAttempts,
    int BackoffInitialDelaySeconds,
    int BackoffMaxDelaySeconds,
    decimal BackoffMultiplier,
    decimal BackoffJitter
);
