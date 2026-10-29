#nullable enable

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// Per-attempt <see cref="JobContext"/> supplied to handlers. Inspection fields come from the
/// claimed row; progress, variable, and sleep operations delegate to the per-routine slice handlers. Every durable
/// write a handler makes through it carries the attempt's <see cref="ExecutionFence"/>.
/// </summary>
internal sealed partial class RuntimeJobContext(
    ClaimedJob job,
    string jobName,
    string namespaceName,
    int namespaceId,
    int leaseTtlSeconds,
    JobsOperations jobOperations,
    SignalsOperations signals,
    AlertsOperations alerts,
    ExecutionOperations executionStore,
    IJobPayloadSerializerRegistry serializers,
    LocksOperations locks,
    FiringEnvelope? firing,
    DateTime? deadlineAtUtc,
    CancellationToken cancellationToken,
    int maxInlinePayloadBytes = int.MaxValue,
    InFlightJob? inFlightJob = null,
    StepRetryDefaults stepRetryDefaults = default,
    ILogger? log = null,
    ActaMetrics? metrics = null,
    string? tenantName = null,
    int workerId = 0,
    Acta.IActaClock? clock = null
) : JobContext
{
    private const string ProgressVariableName = "sys.progress";

    private readonly JobsOperations _jobOperations = jobOperations;
    private readonly SignalsOperations _signals = signals;
    private readonly AlertsOperations _alerts = alerts;
    private readonly ExecutionOperations _executionStore = executionStore;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly LocksOperations _locks = locks;
    private readonly int _namespaceId = namespaceId;
    private readonly int _leaseTtlSeconds = leaseTtlSeconds;
    private readonly int _maxInlinePayloadBytes = maxInlinePayloadBytes;
    private readonly InFlightJob? _inFlightJob = inFlightJob;
    private readonly StepRetryDefaults _stepRetryDefaults = stepRetryDefaults;
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ActaMetrics? _metrics = metrics;
    private readonly Acta.IActaClock? _clock = clock;
    private int _fenced;

    /// <summary>
    /// Whether this attempt's cancellation was the execution-timeout firing rather than an external
    /// cancel, read by <see cref="HandlerInvocation"/> to record the timeout reason, which the retry budget then applies to.
    /// </summary>
    internal bool AttemptTimedOut => _inFlightJob?.TimedOut ?? false;

    /// <summary>
    /// Whether a write found the job no longer held by this attempt. It never clears, and the executor writes no
    /// completion for a fenced attempt, however its handler ended.
    /// </summary>
    internal bool Fenced => Volatile.Read(ref _fenced) == 1;

    private ExecutionFence Fence => new(JobId, ExecutionNumber, WorkerId);

    /// <summary>
    /// Sends one handler-originated write, which carries <see cref="Fence"/>. A refusal marks the attempt fenced and
    /// cancels its token, and every write after it is refused here without reaching the database.
    /// </summary>
    private async Task<T> FencedAsync<T>(Func<Task<T>> write)
    {
        if (Fenced)
        {
            throw new ExecutionFencedException($"Job {JobId} attempt {ExecutionNumber} was already fenced out; the write was not sent.");
        }

        try
        {
            return await write();
        }
        catch (ExecutionFencedException)
        {
            Interlocked.Exchange(ref _fenced, 1);
            _inFlightJob?.Cancel();
            throw;
        }
    }

    private async Task FencedAsync(Func<Task> write) =>
        await FencedAsync(async () =>
        {
            await write();
            return true;
        });

    public override long JobId { get; } = job.JobId;
    public override string JobNamespace { get; } = namespaceName;
    public override int NamespaceId => _namespaceId;
    public override int? TenantId { get; } = job.TenantId;
    public override string? TenantName { get; } = tenantName;
    public override string JobName { get; } = jobName;
    public override JobRef JobRef { get; } = new JobRef(job.JobRef);
    public override int ExecutionNumber { get; } = job.ExecutionNumber;
    public override int WorkerId { get; } = workerId;
    public override CancellationToken CancellationToken { get; } = cancellationToken;
    public override IReadOnlyList<TriggeringSchedule> TriggeringSchedules { get; } = firing?.TriggeringSchedules ?? [];
    public override Guid? FiringId { get; } = firing?.FiringId;
    public override DateTime? DeadlineAtUtc { get; } = deadlineAtUtc;

    private protected override Task SetProgressCoreAsync<T>(T value, CancellationToken ct)
    {
        var payload = JsonSerializer().Serialize(value);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize("progress", payload);
        return FencedAsync(() =>
            CheckpointSlot.SetAsync(_executionStore, Fence, CheckpointKindCode.Progress, ProgressVariableName, payload, ct)
        );
    }

    private protected override Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct)
    {
        var payload = JsonSerializer().Serialize(value);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize($"variable '{name}'", payload);
        return FencedAsync(() => CheckpointSlot.SetAsync(_executionStore, Fence, CheckpointKindCode.Variable, name, payload, ct));
    }

    private protected override Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct)
    {
        ValidatePayloadFormat(payload);
        RejectTopLevelJsonNull(payload);
        EnsureInlineSize($"variable '{name}'", payload);
        return FencedAsync(() => CheckpointSlot.SetAsync(_executionStore, Fence, CheckpointKindCode.Variable, name, payload, ct));
    }

    private protected override async Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct)
        where T : default
    {
        var value = await CheckpointSlot.GetAsync(_executionStore, Fence, CheckpointKindCode.Variable, name, ct);
        return value is null ? (false, default) : (true, Deserialize<T>(value));
    }

    private protected override async Task<T> GetOrSetVariableCoreAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct
    )
    {
        var existing = await CheckpointSlot.GetAsync(_executionStore, Fence, CheckpointKindCode.Variable, name, ct);
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
        var stored = await FencedAsync(() =>
            CheckpointSlot.GetOrSetAsync(_executionStore, Fence, CheckpointKindCode.Variable, name, payload, ct)
        );
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
    private protected override async Task<DateTime> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        // The DB clock, not the host's: the slot dues this deadline is compared with are stamped by the
        // database, so measuring the group against anything else would import the worker's skew.
        var nowUtc = await Clock().GetUtcNowAsync(ct);
        var stored = await FencedAsync(() =>
            CheckpointSlot.GetOrSetAsync(
                _executionStore,
                Fence,
                CheckpointKindCode.Variable,
                name,
                JsonSerializer().Serialize((nowUtc + timeout).Ticks),
                ct
            )
        );
        return new DateTime(Deserialize<long>(stored), DateTimeKind.Utc);
    }

    private Acta.IActaClock Clock() =>
        _clock ?? throw new InvalidOperationException("Bounded group waits need the Acta clock; this context was built without one.");

    private protected override Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct) =>
        CheckpointSlot.ExistsAsync(_executionStore, Fence, CheckpointKindCode.Variable, name, ct);

    private protected override Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct) =>
        FencedAsync(() => CheckpointSlot.DeleteAsync(_executionStore, Fence, CheckpointKindCode.Variable, name, ct));

    private protected override Task ResetStateCoreAsync(CancellationToken ct) =>
        FencedAsync(() => _jobOperations.ResetJobStateAsync(Fence, ct));

    private protected override async Task SleepCoreAsync(
        string name,
        TimeSpan? delay,
        DateTime? resumeAtUtc,
        string? reason,
        CancellationToken ct
    )
    {
        var delaySeconds = delay is { } d ? (int)d.TotalSeconds : (int?)null;
        var decision = await FencedAsync(() =>
            _executionStore.ArmOrConsumeSleepTimerAsync(new Acta.ArmOrConsumeSleepTimerCommand(Fence, name, delaySeconds, resumeAtUtc), ct)
        );
        switch (decision.Outcome)
        {
            case ArmOrConsumeSleepTimerOutcomeCode.Continue:
                return;
            case ArmOrConsumeSleepTimerOutcomeCode.Suspend:
                // The host writes the stored due to jobs_state.next_run_at_utc and finalizes the attempt Suspended.
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
    private protected override Task<SignalSlotState> WaitSignalCoreAsync(
        string name,
        int? timeoutSeconds,
        bool resumeOnTimeout,
        CancellationToken ct
    ) => WaitSlotAsync(name, timeoutSeconds, dueAtUtc: null, resumeOnTimeout, ct);

    private protected override Task<SignalSlotState> WaitSignalUntilCoreAsync(string name, DateTime dueAtUtc, CancellationToken ct) =>
        WaitSlotAsync(name, timeoutSeconds: null, dueAtUtc, resumeOnTimeout: true, ct);

    private async Task<SignalSlotState> WaitSlotAsync(
        string name,
        int? timeoutSeconds,
        DateTime? dueAtUtc,
        bool resumeOnTimeout,
        CancellationToken ct
    )
    {
        var kind = name.StartsWith(RaiseChildLatch.NamePrefix, StringComparison.Ordinal)
            ? CheckpointKindCode.ChildLatch
            : CheckpointKindCode.Signal;
        var decision = await FencedAsync(() => _signals.WaitSignalAsync(Fence, kind, name, timeoutSeconds, dueAtUtc, ct));
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
    /// The wait timed out, so the child and everything under it is work nobody will read. Reason wait-timed-out, not
    /// parent-cancelled: the parent was not cancelled. Every node's cancel carries this attempt's fence, since the
    /// attempt can lose the parent after its wait expired, and a newer attempt may have restarted that child and wait
    /// on it. The walk is follow-up transactions, not atomic with the Expired flip: only the parent replaying this wait
    /// repairs a walk a crash cut short, and a parent that lands terminal without replaying strands the rest
    /// (docs/known-limitations.md).
    /// </summary>
    private protected override async Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct)
    {
        // Parentage is a safety rail, not an optimization. A wait on an id that is not this job's child
        // can only ever expire, because nothing will raise a latch nobody writes, so a handler bug or a
        // stale id reaches this method by construction, not by chance. Without the check it would cancel
        // an unrelated job, in another namespace or tenant, irreversibly and silently.
        var children = (await _executionStore.GetChildJobIdsAsync(JobId, ct)).ChildJobIds;
        if (!children.Contains(childJobId))
        {
            // The awaiting job and its namespace are already on the log scope, so the detail names only the
            // target, by its numeric id: resolving a ref would cost a store round trip on a path whose whole
            // point is to be the cheap safety rail. One warning, not a throw: the wait did time out, and a
            // handler bug should be loud in telemetry rather than destructive in the ledger.
            if (_log.IsEnabled(LogLevel.Warning))
            {
                ActaLog.ChildWaitCancelSkipped(_log, childJobId.ToString(CultureInfo.InvariantCulture));
            }
            return;
        }

        var input = new JobControlInput(null, EventReason.WaitTimedOut, "Parent's child wait timed out.");

        // The child's own latch on this job is Expired, so no raise is needed or possible here; the
        // descendants' latches sit on parents this walk has already made terminal.
        await FencedAsync(() => _jobOperations.CancelTreeAsync(childJobId, input, Fence, ct));
    }

    private protected override T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value)
        where T : default
    {
        var serializer = _serializers.Resolve(valueFormatId);
        var payload = JobPayload.Trusted(serializer.Format, value);
        return serializer.Deserialize<T>(payload);
    }

    private protected override Task<EnqueueJobResponse> StartChildCoreAsync<TInput>(
        TInput input,
        JobEnqueueOptions options,
        CancellationToken ct
    ) => FencedAsync(() => _jobOperations.EnqueueChildAsync(input, options, Fence, ct).AsTask());

    private protected override Task<EnqueueJobResponse> StartChildCoreAsync(EnqueueJobRequest request, CancellationToken ct) =>
        FencedAsync(() => _jobOperations.EnqueueChildAsync(request, Fence, ct).AsTask());

    private protected override async Task<(bool Stored, TResult? Value)> ReadChildResultCoreAsync<TResult>(
        long childJobId,
        int? executionNumber,
        CancellationToken ct
    )
        where TResult : default
    {
        if (await _jobOperations.ReadResultAsync(childJobId, executionNumber, ct) is not { } row)
        {
            return (false, default);
        }
        var serializer = _serializers.Resolve(row.FormatId);
        return (true, serializer.Deserialize<TResult>(JobPayload.Trusted(serializer.Format, row.Data.ToArray())));
    }

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
        return FencedAsync(() => _executionStore.RecordNoteAsync(Fence, message.Truncate(ActaTextLimits.EventMessage)!, payload, ct));
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
        var payload = JobPayload.Trusted(serializer.Format, row.Value);
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
/// The one generic CRUD operation over a <c>checkpoints</c> slot (user variables and the progress
/// slot): last-writer-wins set, get, atomic get-or-set, exists, and delete, dispatched by
/// <see cref="AccessCheckpointAction"/> through <see cref="ExecutionOperations.AccessCheckpointAsync"/>. Checkpoint
/// kinds with their own concurrency choreography (signals, timers, child latches) keep dedicated
/// operations; plain slot CRUD shares this one shape. Caller owns name validation, kind selection,
/// and typed serialization; this op owns the durable row shape. Set, get-or-set, and delete land only under the fence.
/// </summary>
internal static class CheckpointSlot
{
    public static Task SetAsync(
        ExecutionOperations store,
        ExecutionFence fence,
        CheckpointKindCode kind,
        string name,
        JobPayload payload,
        CancellationToken ct
    )
    {
        EnsureWritable(payload);
        return Run(store, AccessCheckpointAction.Set, fence, kind, name, payload, ct);
    }

    public static async Task<CheckpointValue?> GetAsync(
        ExecutionOperations store,
        ExecutionFence fence,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    )
    {
        var row = await Run(store, AccessCheckpointAction.Get, fence, kind, name, null, ct);
        return row.Found == 0 ? null : ToValue(row);
    }

    public static async Task<CheckpointValue> GetOrSetAsync(
        ExecutionOperations store,
        ExecutionFence fence,
        CheckpointKindCode kind,
        string name,
        JobPayload payload,
        CancellationToken ct
    )
    {
        EnsureWritable(payload);
        var row = await Run(store, AccessCheckpointAction.GetOrSet, fence, kind, name, payload, ct);
        return row.Found == 0
            ? throw new InvalidOperationException("access_checkpoint get-or-set returned no stored value.")
            : ToValue(row);
    }

    public static async Task<bool> ExistsAsync(
        ExecutionOperations store,
        ExecutionFence fence,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    ) => (await Run(store, AccessCheckpointAction.Exists, fence, kind, name, null, ct)).Found != 0;

    public static async Task<bool> DeleteAsync(
        ExecutionOperations store,
        ExecutionFence fence,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    ) => (await Run(store, AccessCheckpointAction.Delete, fence, kind, name, null, ct)).Found != 0;

    // A variable value must carry a real payload: a non-None format with a non-zero id.
    private static void EnsureWritable(JobPayload payload)
    {
        if (payload.IsNone || payload.Format.Id == 0)
        {
            throw new ArgumentException("Variable payload cannot be None.", nameof(payload));
        }
    }

    private static Task<AccessCheckpointResponse> Run(
        ExecutionOperations store,
        AccessCheckpointAction action,
        ExecutionFence fence,
        CheckpointKindCode kind,
        string name,
        JobPayload? payload,
        CancellationToken ct
    ) =>
        store.AccessCheckpointAsync(
            new AccessCheckpointCommand(action, fence, kind, name, payload?.Format.Id ?? 0, payload?.Data.ToArray()),
            ct
        );

    private static CheckpointValue ToValue(AccessCheckpointResponse row)
    {
        return row.ValueFormatId is not { } format || row.Value is not { } value || row.Version is not { } version
            ? throw new InvalidOperationException("access_checkpoint returned a found slot with NULL value columns.")
            : new CheckpointValue(format, value, version);
    }
}
