#nullable enable

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// Per-attempt <see cref="JobContext"/> supplied to handlers. Inspection fields come from the
/// claimed row; progress, variable, and sleep operations delegate to the per-routine slice handlers.
/// </summary>
internal sealed partial class RuntimeJobContext(
    ClaimedJob job,
    string jobName,
    string namespaceName,
    int namespaceId,
    int leaseTtlSeconds,
    IJobsOperations jobOperations,
    ISignalsOperations signals,
    IAlertsOperations alerts,
    IExecutionOperations executionStore,
    IJobPayloadSerializerRegistry serializers,
    ILocksOperations locks,
    IReadOnlyList<string> triggeringScheduleNames,
    DateTime? deadlineAtUtc,
    CancellationToken cancellationToken,
    int maxInlinePayloadBytes = int.MaxValue,
    InFlightJob? inFlightJob = null,
    StepRetryDefaults stepRetryDefaults = default,
    ILogger? log = null,
    ActaMetrics? metrics = null,
    IJobs? jobs = null,
    string? tenantKey = null,
    int workerId = 0,
    Acta.IActaClock? clock = null
) : JobContext
{
    private const string ProgressVariableName = "sys.progress";

    private readonly IJobsOperations _jobOperations = jobOperations;
    private readonly ISignalsOperations _signals = signals;
    private readonly IAlertsOperations _alerts = alerts;
    private readonly IExecutionOperations _executionStore = executionStore;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly ILocksOperations _locks = locks;
    private readonly int _namespaceId = namespaceId;
    private readonly int _leaseTtlSeconds = leaseTtlSeconds;
    private readonly int _maxInlinePayloadBytes = maxInlinePayloadBytes;
    private readonly InFlightJob? _inFlightJob = inFlightJob;
    private readonly StepRetryDefaults _stepRetryDefaults = stepRetryDefaults;
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ActaMetrics? _metrics = metrics;
    private readonly IJobs? _jobs = jobs;
    private readonly Acta.IActaClock? _clock = clock;

    /// <summary>
    /// Whether this attempt's cancellation was the execution-timeout firing rather than an external
    /// cancel, read by <see cref="HandlerInvocation"/> to record the timeout reason, which the retry budget then applies to.
    /// </summary>
    internal bool AttemptTimedOut => _inFlightJob?.TimedOut ?? false;

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

    private protected override Task SetProgressCoreAsync<T>(T value, CancellationToken ct)
    {
        var payload = JsonSerializer().Serialize(value);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize("progress", payload);
        return CheckpointSlot.SetAsync(_executionStore, JobId, CheckpointKindCode.Progress, ProgressVariableName, payload, ct);
    }

    private protected override Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct)
    {
        var payload = JsonSerializer().Serialize(value);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize($"variable '{name}'", payload);
        return CheckpointSlot.SetAsync(_executionStore, JobId, CheckpointKindCode.Variable, name, payload, ct);
    }

    private protected override Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct)
    {
        ValidatePayloadFormat(payload);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize($"variable '{name}'", payload);
        return CheckpointSlot.SetAsync(_executionStore, JobId, CheckpointKindCode.Variable, name, payload, ct);
    }

    private protected override async Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct)
        where T : default
    {
        var value = await CheckpointSlot.GetAsync(_executionStore, JobId, CheckpointKindCode.Variable, name, ct);
        return value is null ? (false, default) : (true, Deserialize<T>(value));
    }

    private protected override async Task<T> GetOrSetVariableCoreAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct
    )
    {
        var existing = await CheckpointSlot.GetAsync(_executionStore, JobId, CheckpointKindCode.Variable, name, ct);
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
        var stored = await CheckpointSlot.GetOrSetAsync(_executionStore, JobId, CheckpointKindCode.Variable, name, payload, ct);
        return Deserialize<T>(stored);
    }

    /// <summary>
    /// One deadline for a whole group wait, stored under a reserved sys. slot name so it can never
    /// collide with a user variable. Written once and read forever after: the get-or-set upsert
    /// returns whatever landed first, so a replay, a crash, or a second call with a different
    /// timeout all resolve to the same instant and the group's budget can never restart. Ticks
    /// rather than a serialized DateTime, so the stored bytes are exact and carry no format or kind
    /// ambiguity across a round trip.
    /// </summary>
    private protected override async Task<WaitDeadline> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        // The DB clock, not the host's: the slot dues this deadline is spent against are stamped by the
        // database, so measuring the group against anything else would import the worker's skew. The
        // anchor is stamped the moment the reading lands so elapsed-since covers exactly what the
        // reading does not - including the checkpoint write below.
        var nowUtc = await Clock().GetUtcNowAsync(ct);
        var anchor = Stopwatch.GetTimestamp();
        var stored = await CheckpointSlot.GetOrSetAsync(
            _executionStore,
            JobId,
            CheckpointKindCode.Variable,
            name,
            JsonSerializer().Serialize((nowUtc + timeout).Ticks),
            ct
        );
        return new WaitDeadline(new DateTime(Deserialize<long>(stored), DateTimeKind.Utc), nowUtc, anchor);
    }

    private Acta.IActaClock Clock() =>
        _clock ?? throw new InvalidOperationException("Bounded group waits need the Acta clock; this context was built without one.");

    private protected override Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct) =>
        CheckpointSlot.ExistsAsync(_executionStore, JobId, CheckpointKindCode.Variable, name, ct);

    private protected override Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct) =>
        CheckpointSlot.DeleteAsync(_executionStore, JobId, CheckpointKindCode.Variable, name, ct);

    private protected override Task ResetStateCoreAsync(CancellationToken ct) => _jobOperations.ResetJobStateAsync(JobId, ct);

    private protected override async Task SleepCoreAsync(
        string name,
        TimeSpan? delay,
        DateTime? resumeAtUtc,
        string? reason,
        CancellationToken ct
    )
    {
        var delaySeconds = delay is { } d ? (int)d.TotalSeconds : (int?)null;
        var decision = await _executionStore.ArmOrConsumeSleepTimerAsync(
            new Acta.ArmOrConsumeSleepTimerCommand(JobId, name, delaySeconds, resumeAtUtc),
            ct
        );
        switch (decision.Outcome)
        {
            case ArmOrConsumeSleepTimerOutcomeCode.Continue:
                return;
            case ArmOrConsumeSleepTimerOutcomeCode.Suspend:
                // The host writes the stored due to runtimes.next_run_at_utc and finalizes the attempt Suspended.
                throw new SuspendSignal(decision.DueAtUtc!.Value, reason);
            default:
                throw new InvalidOperationException(
                    $"Job {JobId} already has a pending sleep; only one concurrent sleep is allowed per job."
                );
        }
    }

    private protected override Task<SignalSlotState> WaitSignalCoreAsync(string name, CancellationToken ct) =>
        WaitSignalCoreAsync(name, timeoutSeconds: null, resumeOnTimeout: false, ct);

    /// <summary>
    /// Which overload the handler called decides what an expired wait does: the slot records only
    /// the absolute expiration, so timeout policy lives in code, and a replay of the other
    /// overload against the same slot is free to decide differently.
    /// </summary>
    private protected override async Task<SignalSlotState> WaitSignalCoreAsync(
        string name,
        int? timeoutSeconds,
        bool resumeOnTimeout,
        CancellationToken ct
    )
    {
        var kind = name.StartsWith(RaiseChildLatch.NamePrefix, StringComparison.Ordinal)
            ? CheckpointKindCode.ChildLatch
            : CheckpointKindCode.Signal;
        var decision = await _signals.WaitSignalAsync(JobId, kind, name, timeoutSeconds, ct);
        return decision.Outcome switch
        {
            WaitSignalOutcomeCode.ContinueSet => new SignalSlotState(decision.ValueFormatId, decision.Value),
            WaitSignalOutcomeCode.SuspendPending => throw new SignalSuspendSignal(name, reasonMessage: null), // The host locks the slot and finalizes the attempt Suspended (or Ready if a raise won the race).
            WaitSignalOutcomeCode.TimedOut when resumeOnTimeout => new SignalSlotState(0, null, TimedOut: true),
            WaitSignalOutcomeCode.TimedOut => throw new WaitTimeoutSignal(name), // The host lands the attempt Cancelled, budget-neutral, like the Strict-deadline path.
            _ => throw new InvalidOperationException($"wait_signal returned an unknown outcome for job {JobId}, signal '{name}'."),
        };
    }

    /// <summary>
    /// The wait timed out, so this parent stopped waiting, and the child and everything under it is work nobody will
    /// read. Reason JobWaitTimedOut rather than JobParentCancelled, because the parent was not cancelled and the
    /// timeline must not say it was. Follow-up transactions, like every cascade, not atomic with the Expired flip
    /// that preceded them: a crash mid-walk leaves live stragglers no maintenance pass sweeps, and the only repair is
    /// the parent replaying this wait, re-deriving TimedOut off the Expired slot, and re-running the cancel. A parent
    /// that lands terminal without replaying strands them (docs/technical/known-limitations.md).
    /// </summary>
    private protected override async Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct)
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
            EventReasonCode.JobWaitTimedOut,
            "Parent's child wait timed out."
        );

        // The child's own latch on this job is Expired, so no raise is needed or possible here; the
        // descendants' latches sit on parents this walk has already made terminal.
        await _jobOperations.CancelTreeAsync(childJobId, input, ct);
    }

    private protected override T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value)
        where T : default
    {
        var serializer = _serializers.Resolve(valueFormatId);
        var payload = JobPayload.FromBytes(serializer.Format, value);
        return serializer.Deserialize<T>(payload);
    }

    private protected override async Task<EnqueueJobResponse> StartChildCoreAsync<TInput>(
        TInput input,
        JobEnqueueOptions options,
        CancellationToken ct
    ) => await Jobs().EnqueueAsync(input, options, ct);

    private protected override async Task<EnqueueJobResponse> StartChildCoreAsync(EnqueueJobRequest request, CancellationToken ct) =>
        await Jobs().EnqueueAsync(request, ct);

    private protected override async Task<TResult?> GetChildResultCoreAsync<TResult>(long childJobId, CancellationToken ct)
        where TResult : default => await Jobs().GetResultAsync<TResult>(JobLookup.ById(childJobId), ct);

    private IJobs Jobs() =>
        _jobs ?? throw new InvalidOperationException("Child job operations need the IJobs facade; this context was built without one.");

    private protected override Task WriteNoteCoreAsync<T>(string message, T? detail, CancellationToken ct)
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
        return _executionStore.RecordNoteAsync(JobId, ExecutionNumber, message.Truncate(ActaTextLimits.ReasonMessage)!, payload, ct);
    }

    private protected override async Task RaiseAlertCoreAsync(
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    )
    {
        await _alerts.RaiseAlertAsync(
            RaiseAlertCommand.Manual(JobNamespace, JobId, severityCode, title, message, channelName, deduplicationKey),
            ct
        );
    }

    /// <summary>
    /// Caller-controlled handler writes (variables, progress) HARD-THROW past the inline cap; the
    /// write never reaches storage. Handler results take a separate warn-and-drop path in
    /// HandlerInvocation.
    /// </summary>
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
}

/// <summary>
/// The parent Job's resolved retry policy, supplied to <see cref="RuntimeJobContext"/> so an inline
/// step can resolve its effective policy live each attempt (parent defaults + per-step
/// <c>configure</c> overrides). Never persisted; re-resolved on every replay.
/// </summary>
internal readonly record struct StepRetryDefaults(
    int MaxAttempts,
    int BackoffInitialDelaySeconds,
    int BackoffMaxDelaySeconds,
    decimal BackoffMultiplier,
    decimal BackoffJitter
);
