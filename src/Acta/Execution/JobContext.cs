using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta;

/// <summary>
/// Handler-facing per-attempt context (<c>ctx</c>). Exposes the job's identity, the per-attempt
/// cancellation token, and substrate operations like <see cref="SetProgressAsync{T}"/>. Tests
/// subclass; <c>RuntimeJobContext</c> is the production implementation.
/// </summary>
/// <remarks>
/// Steps and signals are durable and replay-safe, timers (<see cref="SleepAsync"/>) suspend the
/// job budget-neutrally, and <see cref="RunWithLockAsync(string, Func{Task}, TimeSpan?, LockScope, CancellationToken)"/>
/// runs a critical section under a mutual-exclusion lease. Concrete subclasses supply the storage
/// behind each <c>Core</c> sink.
/// </remarks>
public abstract class JobContext
{
    protected JobContext() { }

    /// <summary>Public Job handle (the sequence-allocated <c>job.id</c>).</summary>
    public abstract long JobId { get; }

    /// <summary>Owning <c>JobNamespace</c> (kebab).</summary>
    public abstract string JobNamespace { get; }

    /// <summary>
    /// Owning <c>JobNamespace</c> id: the DB-assigned numeric id, not the kebab
    /// <see cref="JobNamespace"/> name. Used by system jobs that invoke namespace-scoped routines.
    /// </summary>
    public abstract short NamespaceId { get; }

    /// <summary>
    /// Optional tenant this Job is <em>about</em>: the DB-assigned <c>tenants</c> id resolved from the
    /// enqueue <c>TenantKey</c>, or <c>null</c> when the Job has no tenant (including system Jobs).
    /// Inherited from the parent on child Jobs unless the child supplied its own tenant. Read-only scope
    /// for handler branching; it is not a claim or scheduling key.
    /// </summary>
    public virtual int? TenantId => null;

    /// <summary>
    /// External tenant key of <see cref="TenantId"/> (the opaque enqueue <c>TenantKey</c>), or
    /// <c>null</c> when the Job has no tenant. Resolved once per distinct tenant per process and
    /// cached; the authoritative identity for tenant-aware application services during execution.
    /// </summary>
    public virtual string? TenantKey => null;

    /// <summary>Descriptor name (kebab).</summary>
    public abstract string JobName { get; }

    /// <summary>
    /// The job's public stable reference. Default <c>default(JobRef)</c> for test contexts that do
    /// not set it.
    /// </summary>
    public virtual JobRef JobRef => default;

    /// <summary>
    /// Per-attempt cancellation token; cancels on lease expiry, host shutdown, or descriptor
    /// <c>ExecutionTimeout</c>.
    /// </summary>
    public abstract CancellationToken CancellationToken { get; }

    /// <summary>
    /// For a recurring fire, the names of the schedules that were due and coalesced into this
    /// execution, ordered by name. Empty for non-recurring jobs.
    /// </summary>
    public virtual IReadOnlyList<string> TriggeringScheduleNames => [];

    /// <summary>
    /// Which attempt this is: <c>1</c> on the first execution, incrementing on every retry and on
    /// every reclaim after a worker died mid-attempt. The same number the ledger records as
    /// <c>execution_number</c>, so <c>(JobId, ExecutionNumber)</c> identifies this attempt in the
    /// timeline and makes a stable idempotency key for an external call. Defaults to <c>1</c> for
    /// test contexts that do not set it.
    /// </summary>
    /// <example>
    /// <code>
    /// if (ctx.ExecutionNumber > 1)
    /// {
    ///     await ctx.NoteAsync($"retrying after {ctx.ExecutionNumber - 1} failed attempts");
    /// }
    /// </code>
    /// </example>
    public virtual int ExecutionNumber => 1;

    /// <summary>
    /// The <c>workers</c> row id of the worker process running this attempt, for log correlation and
    /// for a handler that wants to stamp its own execution into a note. <c>0</c> when no worker owns
    /// the context, which is the case only in test contexts. Not an affinity handle: the next attempt
    /// of this job may run on any worker, and nothing in Acta promises otherwise.
    /// </summary>
    public virtual int WorkerId => 0;

    /// <summary>
    /// Absolute UTC deadline for the whole job (job creation plus the definition's deadline), or null
    /// when no deadline is configured. Anchored to job creation, so it is stable across retries.
    /// </summary>
    public virtual DateTime? DeadlineAtUtc => null;

    /// <summary>
    /// Whether the job is past its deadline now. False when no deadline is configured.
    /// </summary>
    public bool IsOverdue => DeadlineAtUtc is { } due && due <= DateTime.UtcNow;

    /// <summary>
    /// Time remaining until the deadline; negative when overdue, null when no deadline is configured.
    /// </summary>
    public TimeSpan? TimeUntilDeadline => DeadlineAtUtc is { } due ? due - DateTime.UtcNow : null;

    /// <summary>
    /// Reports progress as a typed value (JSON-serialized, written to the reserved <c>sys.progress</c>
    /// <c>JobCheckpoint</c> progress slot). Last write wins. Silent: no event, no version bump.
    /// </summary>
    /// <example>
    /// <code>
    /// await ctx.SetProgressAsync(42);
    /// await ctx.SetProgressAsync("Stage 2: validating");
    /// await ctx.SetProgressAsync(new MyProgress(42, "Halfway"));
    /// </code>
    /// </example>
    public async Task SetProgressAsync<T>(T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SetProgressCoreAsync(value, linked.Token);
    }

    /// <summary>
    /// Subclass sink for <see cref="SetProgressAsync{T}"/>. The subclass serializes through its payload
    /// serializer (so source-gen JSON is honored under Native AOT) rather than the reflection-based
    /// static helper.
    /// </summary>
    protected abstract Task SetProgressCoreAsync<T>(T value, CancellationToken ct);

    // ---------- Variables ----------

    /// <summary>
    /// Set a durable per-job variable to a non-null JSON value. Last write wins.
    /// <para>
    /// Use this rather than a field or a static: a job can be resumed by a different process, so
    /// state that must survive a retry or a crash belongs in the ledger, not in memory.
    /// </para>
    /// </summary>
    public async Task SetVariableAsync<T>(string name, T value, CancellationToken ct = default)
        where T : notnull
    {
        name = ValidateUserVariableName(name);
        ArgumentNullException.ThrowIfNull(value);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SetVariableCoreAsync(name, value, linked.Token);
    }

    /// <summary>
    /// Set a durable per-job variable using an exact payload format and byte buffer.
    /// </summary>
    public async Task SetVariableAsync(string name, JobPayload payload, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        if (payload.IsNone)
        {
            throw new ArgumentException("Variable payload cannot be None.", nameof(payload));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SetVariableCoreAsync(name, payload, linked.Token);
    }

    /// <summary>
    /// Get a durable per-job variable, or <c>null</c> when the variable is absent.
    /// </summary>
    public async Task<T?> GetVariableOrDefaultAsync<T>(string name, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var (found, value) = await TryGetVariableCoreAsync<T>(name, linked.Token);
        return found ? value : default;
    }

    /// <summary>
    /// Get a durable per-job variable, or <paramref name="defaultValue"/> when the variable is absent.
    /// </summary>
    public async Task<T> GetVariableOrDefaultAsync<T>(string name, T defaultValue, CancellationToken ct = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        // One read that reports existence: a value-type T cannot carry "absent" as null, so a stored
        // value-type default (e.g. 0) must not be mistaken for an absent variable.
        var (found, value) = await TryGetVariableCoreAsync<T>(name, linked.Token);
        return found ? value! : defaultValue;
    }

    /// <summary>
    /// Get a durable per-job variable, or throw when the variable is absent.
    /// </summary>
    public async Task<T> GetRequiredVariableAsync<T>(string name, CancellationToken ct = default)
        where T : notnull
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var (found, value) = await TryGetVariableCoreAsync<T>(name, linked.Token);
        return found ? value! : throw new InvalidOperationException($"Job variable '{name}' does not exist.");
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="GetOrSetVariableAsync{T}(string, Func{CancellationToken, Task{T}}, CancellationToken)"/>
    /// taking a synchronous factory.
    /// </summary>
    public Task<T> GetOrSetVariableAsync<T>(string name, Func<T> valueFactory, CancellationToken ct = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return GetOrSetVariableAsync(name, _ => Task.FromResult(valueFactory()), ct);
    }

    /// <summary>
    /// Get a durable per-job variable, or compute and store a non-null JSON value when absent.
    /// </summary>
    public async Task<T> GetOrSetVariableAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct = default
    )
        where T : notnull
    {
        name = ValidateUserVariableName(name);
        ArgumentNullException.ThrowIfNull(valueFactory);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await GetOrSetVariableCoreAsync(name, valueFactory, linked.Token);
    }

    /// <summary>
    /// True when a durable per-job variable exists.
    /// </summary>
    public async Task<bool> ExistsVariableAsync(string name, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await ExistsVariableCoreAsync(name, linked.Token);
    }

    /// <summary>
    /// Delete a durable per-job variable. Returns <c>true</c> when a row was removed.
    /// </summary>
    public async Task<bool> DeleteVariableAsync(string name, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await DeleteVariableCoreAsync(name, linked.Token);
    }

    private static string ValidateUserVariableName(string name) =>
        IdentifierSyntax.CanonicalizeUserDottedKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);

    /// <summary>
    /// Subclass sink: set a user variable using the default JSON format.
    /// </summary>
    protected abstract Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct)
        where T : notnull;

    /// <summary>
    /// Subclass sink: set a user variable using an exact payload.
    /// </summary>
    protected abstract Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct);

    /// <summary>
    /// Subclass sink: get a user variable in one read, reporting whether it was found. The found flag is
    /// what distinguishes an absent variable from a stored value-type default (e.g. <c>0</c> for int).
    /// </summary>
    protected abstract Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink: get a user variable, or compute and store it when absent.
    /// </summary>
    protected abstract Task<T> GetOrSetVariableCoreAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct
    )
        where T : notnull;

    /// <summary>
    /// Subclass sink: check whether a user variable exists.
    /// </summary>
    protected abstract Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink: delete a user variable.
    /// </summary>
    protected abstract Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct);

    // ---------- State reset ----------

    /// <summary>
    /// Clears this Job's durable state (every <c>JobCheckpoint</c>, <c>JobStep</c>, and
    /// <c>JobResult</c> row) so the next execution starts as new. Does not change the Job's status,
    /// lease, failure budget, or schedule; the current attempt still completes normally. Emits
    /// <c>job.state-reset</c>.
    /// </summary>
    /// <remarks>
    /// Intended as the final action of a handler that runs again (a recurring Job, or one that
    /// re-arms): the attempt completes and the next claim sees none of this attempt's state. Calling
    /// it mid-handler discards the variables, timers, step checkpoints, and signals the rest of the
    /// attempt would read. Child outcome latches are cleared too, while a finished child still dedupes
    /// by name and never re-raises, so waiting on it again hangs; use per-fire child names in handlers
    /// that reset. It also breaks at-most-once: the reset deletes an already-run <c>AtMostOnce</c>
    /// step's recorded outcome, so an attempt aborted afterwards re-arms, finds no record, and invokes
    /// that step a second time. Mid-execution this is for tests and repair, never a handler's path.
    /// </remarks>
    public async Task ResetStateAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await ResetStateCoreAsync(linked.Token);
    }

    /// <summary>
    /// Subclass sink: clear the Job's durable state (variables, timers, steps, signals, results).
    /// </summary>
    protected abstract Task ResetStateCoreAsync(CancellationToken ct);

    // ---------- Reschedule / Sleep ----------

    /// <summary>
    /// Re-arms this Job to run again after <paramref name="delay"/> and stops the current attempt
    /// without charging the failure budget. Throws <see cref="RescheduleJobException"/> synchronously;
    /// code after this call in the current attempt is unreachable. The host computes the due instant
    /// from DB UTC time (<c>db_now + delay</c>).
    /// </summary>
    /// <remarks>
    /// The <see cref="Task"/> return type mirrors <see cref="SleepAsync"/> for call-site symmetry; the
    /// throw is synchronous, so a forgotten <c>await</c> still stops the handler.
    /// </remarks>
    public Task RescheduleAsync(TimeSpan delay, string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new RescheduleJobException(delay, reasonMessage);
    }

    /// <summary>
    /// Absolute-instant variant of <see cref="RescheduleAsync"/>: re-arms this Job to run again at
    /// <paramref name="resumeAtUtc"/>. A past instant re-arms as immediately claimable; throw and
    /// budget semantics match <see cref="RescheduleAsync"/>.
    /// </summary>
    public Task RescheduleUntilAsync(DateTimeOffset resumeAtUtc, string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new RescheduleJobException(resumeAtUtc, reasonMessage);
    }

    // ---------- Handler-initiated control ----------

    /// <summary>
    /// Deliberately ends this Job as terminal <c>Failed</c> for a business reason. Throws
    /// <see cref="HandlerFailException"/> synchronously; code after this call in the current attempt is
    /// unreachable. The attempt is not retried and the failure budget is untouched, distinct from
    /// throwing an exception, which is recorded as an unhandled failure. Use the normal throw or failure
    /// path when retry and backoff are wanted.
    /// </summary>
    /// <remarks>
    /// The <see cref="Task"/> return type mirrors the other control verbs for call-site symmetry; the
    /// throw is synchronous, so a forgotten <c>await</c> still stops the handler.
    /// </remarks>
    public Task FailAsync(string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new HandlerFailException(reasonMessage);
    }

    /// <summary>
    /// Deliberately ends this Job as terminal <c>Cancelled</c>, a non-failure stop (duplicate,
    /// superseded, not-applicable work). Throws <see cref="HandlerCancelException"/> synchronously; code
    /// after this call in the current attempt is unreachable. The attempt is not retried and the failure
    /// budget is untouched. The cancel cascades recursively to this Job's non-terminal descendant
    /// subtree (descendants land <c>Cancelled</c> with <c>reason = ParentCancelled</c>).
    /// </summary>
    public Task CancelAsync(string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new HandlerCancelException(reasonMessage);
    }

    /// <summary>
    /// Deliberately holds this Job in <c>Paused</c> until an external resume (manual review, missing
    /// approval, operator intervention). Throws <see cref="HandlerPauseException"/> synchronously; code
    /// after this call in the current attempt is unreachable. The Job is not retried automatically, sets
    /// no <c>next_run_at_utc</c>, and the failure budget is untouched. Resumes only through
    /// <c>IJobs.ResumeAsync</c>. This is operator-driven hold, not framework-managed
    /// <c>Suspended</c> (which <see cref="SleepAsync"/> / <see cref="WaitSignalAsync(string, CancellationToken)"/> produce).
    /// </summary>
    public Task PauseAsync(string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new HandlerPauseException(reasonMessage);
    }

    /// <summary>
    /// Durable, replay-safe named wait. The first pass arms a timer checkpoint and suspends the Job
    /// until <paramref name="delay"/> elapses; the replayed pass after the due instant consumes the
    /// timer and returns so the handler proceeds. A zero <paramref name="delay"/> returns immediately
    /// without arming a timer. Suspending is budget-neutral.
    /// </summary>
    /// <remarks>
    /// Use this rather than <c>Task.Delay</c> or <c>Thread.Sleep</c>: those hold a worker for the whole
    /// duration and are lost if the process restarts, while a durable sleep parks the job and re-arms
    /// it at the due instant, costing no worker in between.
    /// </remarks>
    /// <param name="name">Dotted-kebab wait name, unique per Job; identifies the timer across replays.</param>
    /// <param name="delay">Wait length from DB now; whole-second precision (sub-second rounds up).</param>
    /// <param name="reasonMessage">Operator-readable suspend reason.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task SleepAsync(string name, TimeSpan delay, string? reasonMessage = null, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserDottedKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        var seconds = DurationSyntax.ToWholeSeconds(delay, nameof(delay));
        if (seconds == 0)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SleepCoreAsync(name, TimeSpan.FromSeconds(seconds), resumeAtUtc: null, reasonMessage, linked.Token);
    }

    /// <summary>
    /// Absolute-instant variant of <see cref="SleepAsync"/>: suspends until <paramref name="resumeAtUtc"/>.
    /// Whether the instant is already due is decided by DB UTC time inside the timer routine, not the app
    /// clock, so a past instant consumes immediately and the handler proceeds.
    /// </summary>
    public async Task SleepUntilAsync(string name, DateTimeOffset resumeAtUtc, string? reasonMessage = null, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserDottedKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SleepCoreAsync(name, delay: null, resumeAtUtc.UtcDateTime, reasonMessage, linked.Token);
    }

    /// <summary>
    /// Subclass sink: arms or consumes the named sleep timer. Exactly one of <paramref name="delay"/>
    /// and <paramref name="resumeAtUtc"/> is non-null. Returns normally to continue the handler; throws
    /// the framework suspend signal to re-arm the Job for a subsequent claim.
    /// </summary>
    protected abstract Task SleepCoreAsync(
        string name,
        TimeSpan? delay,
        DateTime? resumeAtUtc,
        string? reasonMessage,
        CancellationToken ct
    );

    // ---------- Signals ----------

    /// <summary>
    /// Durable, replay-safe named wait. Returns immediately when the signal <paramref name="name"/> is
    /// already <c>Set</c>; otherwise arms a <c>Pending</c> slot and suspends the Job until an external
    /// <c>IJobs.RaiseSignalAsync</c> raises it. Replay re-runs the handler from the start; on the replay
    /// the wait returns because the slot is now <c>Set</c>. Suspending is budget-neutral.
    /// </summary>
    /// <param name="name">Kebab-case signal name, unique per Job; identifies the slot across replays.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task WaitSignalAsync(string name, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await WaitSignalCoreAsync(name, linked.Token);
    }

    /// <summary>
    /// Typed-payload variant of <see cref="WaitSignalAsync(string, CancellationToken)"/>. Deserializes the
    /// raised payload through the registered serializer. A presence-only signal (raised without a value)
    /// returns <c>default</c>.
    /// </summary>
    public async Task<T?> WaitSignalAsync<T>(string name, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, linked.Token);
        return outcome.ValueFormatId == 0 ? default : DeserializeSignalPayload<T>(outcome.ValueFormatId, outcome.Value!);
    }

    /// <summary>
    /// The raised slot's stored payload: format id + bytes. <c>ValueFormatId == 0</c> means a
    /// presence-only signal with no payload (<see cref="Value"/> is <c>null</c>).
    /// </summary>
    protected readonly record struct SignalWaitOutcome(byte ValueFormatId, byte[]? Value);

    /// <summary>
    /// Subclass sink: reads or arms the named signal slot. Returns normally with the slot payload when
    /// it is <c>Set</c>; throws the framework signal-suspend signal to re-arm the Job for a subsequent claim.
    /// </summary>
    protected abstract Task<SignalWaitOutcome> WaitSignalCoreAsync(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink: deserializes a raised signal payload via the runtime serializer registry.
    /// </summary>
    protected abstract T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value);

    // ---------- Child jobs ----------

    private const string ChildSignalPrefix = "sys.child.";

    /// <summary>
    /// Enqueues a child job from a typed input, replay-safe: <paramref name="name"/> becomes the
    /// child's deduplication key, sibling-unique per parent, so a replay returns the existing child
    /// (<see cref="JobEnqueueAction.Deduplicated"/>) instead of inserting a duplicate. The child runs
    /// independently; pair with <see cref="WaitChildAsync"/> to join on its completion.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="configure">Optional enqueue options; the parent id and deduplication key are framework-set and win over configured values.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task<JobEnqueueOutcome> StartChildAsync<TInput>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        ArgumentNullException.ThrowIfNull(input);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await StartChildCoreAsync(input, BuildChildOptions(name, configure), linked.Token);
    }

    /// <summary>
    /// Raw-route variant of <see cref="StartChildAsync{TInput}"/> for explicit
    /// (<paramref name="jobNamespace"/>, <paramref name="jobName"/>) targeting, including a namespace
    /// other than this Job's.
    /// </summary>
    public async Task<JobEnqueueOutcome> StartChildAsync(
        string name,
        string jobNamespace,
        string jobName,
        JobPayload input = default,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        var options = BuildChildOptions(name, configure);
        var request = new JobEnqueueRequest(
            jobNamespace,
            jobName,
            input,
            DeduplicationKey: options.DeduplicationKey,
            CorrelationKey: options.CorrelationKey,
            ExclusiveKey: options.ExclusiveKey,
            Priority: options.Priority,
            NextRunAtUtc: options.NextRunAtUtc,
            DelaySeconds: options.DelaySeconds,
            Tags: options.Tags,
            ParentJobId: JobId,
            TenantKey: options.TenantKey,
            OverrideParentTenant: options.OverrideParentTenant
        );

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await StartChildCoreAsync(request, linked.Token);
    }

    /// <summary>
    /// Durable, replay-safe wait for a child started by <see cref="StartChildAsync{TInput}"/> to reach
    /// a terminal status. Returns the child's terminal outcome immediately when it already finished;
    /// otherwise suspends this Job (budget-neutral) until the child's terminal landing releases it.
    /// Never throws on a failed or cancelled child; branch on <see cref="ChildJobOutcome.Succeeded"/>.
    /// The outcome latch lives on this Job and survives the child row's retention purge.
    /// </summary>
    public async Task<ChildJobOutcome> WaitChildAsync(long childJobId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        // Read side of the child-latch checkpoint key. The name is persisted in the ledger and matched
        // as text against the one RaiseChildLatch writes, in another process and possibly another
        // culture, so the two renderings must agree byte for byte; the invariant culture is stated on
        // both sides rather than inherited from whatever the ambient one happens to be.
        var outcome = await WaitSignalCoreAsync(ChildSignalPrefix + childJobId.ToString(CultureInfo.InvariantCulture), linked.Token);
        return outcome.Value is null
            ? throw new InvalidOperationException($"Child outcome slot for job {childJobId} carries no envelope.")
            : ChildOutcomeEnvelope.Parse(outcome.Value);
    }

    /// <summary>
    /// Waits for every listed child to reach a terminal status (all-of), suspending as needed. Outcomes
    /// are returned in the order given. Replay-safe: finished children resolve from their latches
    /// without suspending.
    /// </summary>
    public async Task<IReadOnlyList<ChildJobOutcome>> WaitChildrenAsync(IReadOnlyList<long> childJobIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(childJobIds);
        var outcomes = new ChildJobOutcome[childJobIds.Count];
        for (var i = 0; i < childJobIds.Count; i++)
        {
            outcomes[i] = await WaitChildAsync(childJobIds[i], ct);
        }
        return outcomes;
    }

    /// <summary>
    /// Reads a child's stored result, deserialized to <typeparamref name="TResult"/>; <c>default</c>
    /// when the child stored none or its result row was already purged by retention. Point-in-time and
    /// non-blocking; wait for the child first when ordering matters.
    /// </summary>
    public async Task<TResult?> GetChildResultAsync<TResult>(long childJobId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await GetChildResultCoreAsync<TResult>(childJobId, linked.Token);
    }

    /// <summary>
    /// Starts the named child and durably waits for its terminal outcome in one call, the child-level
    /// mirror of <c>IJobs.RunAndWaitAsync</c>. Returned, never thrown; branch on the outcome or call
    /// <see cref="JobOutcome.ThrowIfFailed"/>. Waits this one child before returning: to fan out, start
    /// every child first and join with <see cref="WaitChildrenAsync"/>.
    /// </summary>
    public async Task<JobOutcome> ExecuteChildAsync<TInput>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        var child = await StartChildAsync(name, input, configure, ct);
        var outcome = await WaitChildAsync(child.JobId, ct);
        return outcome.Status switch
        {
            JobStatusCode.Succeeded => JobOutcome.Succeeded(child.JobId),
            JobStatusCode.Cancelled => JobOutcome.Cancelled(child.JobId),
            _ => JobOutcome.Failed(child.JobId),
        };
    }

    /// <summary>
    /// Result-returning variant of <see cref="ExecuteChildAsync{TInput}"/>: on a successful child the
    /// outcome carries the deserialized result (<see cref="JobOutcome{T}.ValueOrThrow"/> for the terse
    /// path). A child that succeeded without storing a result throws: the caller asked for a typed
    /// result the child's contract does not produce.
    /// </summary>
    public async Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        var child = await StartChildAsync(name, input, configure, ct);
        var outcome = await WaitChildAsync(child.JobId, ct);
        if (outcome.Status != JobStatusCode.Succeeded)
        {
            return outcome.Status == JobStatusCode.Cancelled
                ? JobOutcome<TResult>.Cancelled(child.JobId)
                : JobOutcome<TResult>.Failed(child.JobId);
        }

        var value =
            await GetChildResultAsync<TResult>(child.JobId, ct)
            ?? throw new InvalidOperationException(
                $"Child job {child.JobId} ('{name}') succeeded but stored no result; "
                    + "use the non-result ExecuteChildAsync overload for result-less children."
            );
        return JobOutcome<TResult>.Succeeded(child.JobId, value);
    }

    private JobEnqueueOptions BuildChildOptions(string name, Action<JobEnqueueOptionsBuilder>? configure)
    {
        JobEnqueueOptions? configured = null;
        if (configure is not null)
        {
            var builder = new JobEnqueueOptionsBuilder();
            configure(builder);
            configured = builder.Build();
        }

        return new JobEnqueueOptions
        {
            JobNamespace = configured?.JobNamespace,
            DeduplicationKey = name,
            CorrelationKey = configured?.CorrelationKey,
            ExclusiveKey = configured?.ExclusiveKey,
            Priority = configured?.Priority,
            Tags = configured?.Tags,
            NextRunAtUtc = configured?.NextRunAtUtc,
            DelaySeconds = configured?.DelaySeconds,
            ParentJobId = JobId,
            TenantKey = configured?.TenantKey,
            OverrideParentTenant = configured?.OverrideParentTenant ?? false,
        };
    }

    /// <summary>
    /// Subclass sink: typed child enqueue through the shared enqueue path. The options carry the
    /// framework-set parent id and deduplication key.
    /// </summary>
    protected abstract Task<JobEnqueueOutcome> StartChildCoreAsync<TInput>(TInput input, JobEnqueueOptions options, CancellationToken ct)
        where TInput : notnull;

    /// <summary>
    /// Subclass sink: raw-route child enqueue through the shared enqueue path.
    /// </summary>
    protected abstract Task<JobEnqueueOutcome> StartChildCoreAsync(JobEnqueueRequest request, CancellationToken ct);

    /// <summary>
    /// Subclass sink: read and deserialize the child's stored result.
    /// </summary>
    protected abstract Task<TResult?> GetChildResultCoreAsync<TResult>(long childJobId, CancellationToken ct);

    // ---------- Child-job groups: Map, Parallel, Join ----------

    /// <summary>
    /// Waits for every child handle to reach a terminal status and returns the outcomes in caller
    /// order. A nicer wrapper over <see cref="WaitChildrenAsync"/>: it does not throw because a child
    /// failed and does not cancel siblings; branch on the returned outcome or call
    /// <see cref="JoinOutcome.ThrowIfAnyFailed"/>.
    /// </summary>
    public async Task<JoinOutcome> JoinAsync(IReadOnlyList<JobEnqueueOutcome> children, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(children);

        var ids = new long[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            ids[i] = children[i].JobId;
        }

        var outcomes = await WaitChildrenAsync(ids, ct);
        return new JoinOutcome(outcomes);
    }

    /// <summary>
    /// Starts each named branch as a child job and waits for all of them, returning branch-keyed
    /// outcomes. The group and branch names are validated and branch names must be unique; every
    /// branch is started before any is awaited. Does not throw on a failed branch and does not
    /// fail-fast; branch on the outcome or call <see cref="ParallelOutcome.ThrowIfAnyFailed"/>.
    /// </summary>
    public async Task<ParallelOutcome> ParallelAsync(string groupName, Action<ParallelBuilder> configure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        IdentifierSyntax.ValidateUserKebab(groupName, nameof(groupName), IdentifierSyntax.ExtendedMaxLength);

        var builder = new ParallelBuilder();
        configure(builder);
        var branches = builder.Branches;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            IdentifierSyntax.ValidateUserKebab(branch.BranchName, "branchName", IdentifierSyntax.ExtendedMaxLength);
            if (!seen.Add(branch.BranchName))
            {
                throw new ArgumentException(
                    $"Parallel group '{groupName}' has a duplicate branch name '{branch.BranchName}'.",
                    nameof(configure)
                );
            }
        }

        var ids = new long[branches.Count];
        for (var i = 0; i < branches.Count; i++)
        {
            var childName = $"{groupName}-{branches[i].BranchName}";
            ids[i] = (await branches[i].Start(this, childName, ct)).JobId;
        }

        var outcomes = await WaitChildrenAsync(ids, ct);

        var byBranch = new Dictionary<string, ChildJobOutcome>(StringComparer.Ordinal);
        for (var i = 0; i < branches.Count; i++)
        {
            byBranch[branches[i].BranchName] = outcomes[i];
        }

        return new ParallelOutcome(groupName, byBranch);
    }

    /// <summary>
    /// Fans out one child job per item and waits for all of them, returning outcomes keyed back to
    /// the original items. The caller supplies a stable item key; the group name plus key derives a
    /// deterministic, parent-scoped child name (readable when the key is name-safe, otherwise a stable
    /// hash), so a replay dedupes onto the same children. Duplicate keys are rejected before any child
    /// is started; every child is started before any is awaited. Does not throw on a failed child and
    /// does not limit runtime worker concurrency; branch on the outcome or call
    /// <see cref="MapOutcome{TKey}.ThrowIfAnyFailed"/>.
    /// </summary>
    public async Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(child);
        IdentifierSyntax.ValidateUserKebab(groupName, nameof(groupName), IdentifierSyntax.ExtendedMaxLength);

        var materialized = items as IReadOnlyList<TItem> ?? items.ToList();
        var keys = new TKey[materialized.Count];
        var names = new string[materialized.Count];
        var seenKeys = new HashSet<TKey>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < materialized.Count; i++)
        {
            var key =
                itemKey(materialized[i])
                ?? throw new ArgumentException($"Map group '{groupName}' produced a null item key.", nameof(itemKey));
            if (!seenKeys.Add(key))
            {
                throw new ArgumentException($"Map group '{groupName}' has a duplicate item key '{key}'.", nameof(itemKey));
            }

            var childName = ChildGroupName(groupName, key);
            if (!seenNames.Add(childName))
            {
                throw new ArgumentException(
                    $"Map group '{groupName}' derived a duplicate child name '{childName}' for key '{key}'.",
                    nameof(itemKey)
                );
            }

            keys[i] = key;
            names[i] = childName;
        }

        var ids = new long[materialized.Count];
        for (var i = 0; i < materialized.Count; i++)
        {
            ids[i] = (await StartChildAsync(names[i], child(materialized[i]), ct: ct)).JobId;
        }

        var outcomes = await WaitChildrenAsync(ids, ct);

        var resultItems = new MapItemOutcome<TKey>[materialized.Count];
        for (var i = 0; i < materialized.Count; i++)
        {
            resultItems[i] = new MapItemOutcome<TKey>(keys[i], ids[i], outcomes[i]);
        }

        return new MapOutcome<TKey>(groupName, resultItems);
    }

    private static string ChildGroupName<TKey>(string groupName, TKey key)
        where TKey : notnull
    {
        var canonical = key.ToString() ?? string.Empty;
        return IsNameSafeTail(canonical) && groupName.Length + 1 + canonical.Length <= IdentifierSyntax.ExtendedMaxLength
            ? $"{groupName}-{canonical}"
            : $"{groupName}-{ShortHash(canonical)}";
    }

    private static bool IsNameSafeTail(string value)
    {
        if (value.Length == 0 || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') || c == '-';
            if (!ok)
            {
                return false;
            }
            if (c == '-' && i > 0 && value[i - 1] == '-')
            {
                return false;
            }
        }
        return true;
    }

    private static string ShortHash(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    // ---------- Steps ----------

    /// <summary>
    /// Runs <paramref name="body"/> as a durable, replay-safe step identified by
    /// <paramref name="name"/>. The first invocation runs the body and records the outcome; on a parent
    /// replay a previously-succeeded step returns without re-running the body. A failure retries with
    /// backoff (re-arming the parent budget-neutrally until the next attempt) until it succeeds or
    /// exhausts its budget, at which point <see cref="StepExhaustedException"/> is thrown.
    /// </summary>
    /// <remarks>
    /// The body must be idempotent: a crash after the side effect runs but before the outcome is
    /// recorded re-runs the body on replay. Only step-wrapped work is replay-skipped; bare handler
    /// code between steps re-runs on every retry.
    /// </remarks>
    /// <param name="name">Stable kebab-case slot name, unique per Job; durable identity across replays.</param>
    /// <param name="body">The side-effecting work; receives a cancellation token linked to the attempt.</param>
    /// <param name="configure">Optional per-step retry overrides; unset fields inherit the parent <c>[Job]</c> policy.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task RunStepAsync(
        string name,
        Func<CancellationToken, Task> body,
        Action<StepOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        ArgumentNullException.ThrowIfNull(body);
        var options = BuildStepOptions(configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await RunStepCoreAsync(name, body, options, linked.Token);
    }

    /// <summary>
    /// Result-returning variant of
    /// <see cref="RunStepAsync(string, Func{CancellationToken, Task}, Action{StepOptionsBuilder}, CancellationToken)"/>.
    /// On replay of a succeeded step the stored result is deserialized into
    /// <typeparamref name="TResult"/> and returned without re-running the body;
    /// <see cref="StepResultContractMismatchException"/> is thrown if the stored result no longer fits.
    /// <para>
    /// Wrap each outside-world effect in its own named step rather than calling it directly in the
    /// handler: jobs are at-least-once, so an unwrapped call repeats on every retry. One effect per
    /// step, because a crash between two effects in one step re-runs both.
    /// </para>
    /// </summary>
    public async Task<TResult> RunStepAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        Action<StepOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        ArgumentNullException.ThrowIfNull(body);
        var options = BuildStepOptions(configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await RunStepCoreAsync(name, body, options, linked.Token);
    }

    private static StepOptions BuildStepOptions(Action<StepOptionsBuilder>? configure)
    {
        if (configure is null)
        {
            return StepOptions.Inherit;
        }

        var builder = new StepOptionsBuilder();
        configure(builder);
        return builder.Build();
    }

    /// <summary>
    /// Subclass sink: runs the void step orchestration (start, body, complete, retry or exhaust).
    /// Returns normally on success; throws the framework step-retry signal to re-arm the Job, or
    /// <see cref="StepExhaustedException"/> when the budget is spent.
    /// </summary>
    protected abstract Task RunStepCoreAsync(string name, Func<CancellationToken, Task> body, StepOptions options, CancellationToken ct);

    /// <summary>
    /// Subclass sink: result-returning step orchestration. Returns the (possibly replayed) result;
    /// throws as described on
    /// <see cref="RunStepCoreAsync(string, Func{CancellationToken, Task}, StepOptions, CancellationToken)"/>.
    /// </summary>
    protected abstract Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        CancellationToken ct
    );

    // ---------- Locking ----------

    private const int LockBackoffBaseMs = 50;
    private const int LockBackoffCapMs = 1000;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="action"/> while holding a mutual-exclusion lock on <paramref name="key"/>.
    /// Acquisition is immediate-then-retry with internal exponential backoff until acquired or the
    /// timeout budget is spent; the lock is released when the action returns or throws.
    /// </summary>
    /// <param name="key">Opaque user lock key (segmented + scoped internally).</param>
    /// <param name="action">The critical section to run under the lock.</param>
    /// <param name="timeout">
    /// Acquisition budget. <c>null</c> uses a default (about 30s); <see cref="TimeSpan.Zero"/> makes a
    /// single immediate attempt with no retry.
    /// </param>
    /// <param name="scope">Namespace-scoped (default) or cluster-wide. See <see cref="LockScope"/>.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    /// <exception cref="LockAcquisitionTimeoutException">The lock was not acquired within the budget.</exception>
    public Task RunWithLockAsync(
        string key,
        Func<Task> action,
        TimeSpan? timeout = null,
        LockScope scope = LockScope.Namespace,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunWithLockAsync(
            key,
            async () =>
            {
                await action();
                return true;
            },
            timeout,
            scope,
            ct
        );
    }

    /// <summary>
    /// Result-returning overload of
    /// <see cref="RunWithLockAsync(string, Func{Task}, TimeSpan?, LockScope, CancellationToken)"/>.
    /// </summary>
    public async Task<TResult> RunWithLockAsync<TResult>(
        string key,
        Func<Task<TResult>> action,
        TimeSpan? timeout = null,
        LockScope scope = LockScope.Namespace,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(action);

        var budget = timeout ?? DefaultLockTimeout;
        if (budget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Lock acquisition timeout cannot be negative.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var lct = linked.Token;
        var deadlineTick = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        var attempt = 0;

        while (true)
        {
            lct.ThrowIfCancellationRequested();

            if (await AcquireLockCoreAsync(key, scope, lct) is { } token)
            {
                try
                {
                    return await action();
                }
                finally
                {
                    // Best-effort release: version-CAS makes it a no-op if the lease was already stolen.
                    // Use None so a cancelled action still releases. Cleanup failure must never replace
                    // the action outcome; the runtime reports it and the lease TTL provides recovery.
                    try
                    {
                        await ReleaseLockCoreAsync(key, scope, token, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            OnLockReleaseFailure(key, scope, ex);
                        }
                        catch
                        {
                            // Observability is best-effort too; a broken observer cannot alter the action outcome.
                        }
                    }
                }
            }

            var remainingMs = deadlineTick - Environment.TickCount64;
            if (budget == TimeSpan.Zero || remainingMs <= 0)
            {
                throw new LockAcquisitionTimeoutException(key, budget);
            }

            await Task.Delay(NextLockBackoffMs(attempt++, remainingMs), lct);
        }
    }

    // Exponential (base doubled per attempt), capped, full-jittered, clamped to the remaining budget.
    private static int NextLockBackoffMs(int attempt, long remainingMs)
    {
        var exp = LockBackoffBaseMs * (1L << Math.Min(attempt, 20));
        var capped = (int)Math.Min(exp, LockBackoffCapMs);
        var jittered = Random.Shared.Next(capped / 2, capped + 1);
        return (int)Math.Clamp(jittered, 1, remainingMs);
    }

    /// <summary>
    /// Subclass sink: a single no-wait acquire attempt for the scoped <paramref name="key"/>.
    /// Returns the per-hold token on success, or <c>null</c> when the lock is busy.
    /// </summary>
    protected abstract Task<Guid?> AcquireLockCoreAsync(string key, LockScope scope, CancellationToken ct);

    /// <summary>
    /// Subclass sink: release the lock held under <paramref name="holdToken"/> for the scoped
    /// <paramref name="key"/>.
    /// </summary>
    protected abstract Task ReleaseLockCoreAsync(string key, LockScope scope, Guid holdToken, CancellationToken ct);

    /// <summary>
    /// Optional runtime observability hook for a best-effort release failure. Implementations must not
    /// throw; the wrapper defensively suppresses observer failures to preserve the handler outcome.
    /// </summary>
    protected virtual void OnLockReleaseFailure(string key, LockScope scope, Exception exception) { }

    // ---------- Alerts ----------

    /// <summary>
    /// Persists an operator-facing alert from inside the handler. The framework stamps the origin
    /// (<c>source = Manual</c>, <c>reason = Manual</c>, <c>delivery = Pending</c>) and the job context
    /// (namespace, job). A non-null <paramref name="deduplicationKey"/> collapses repeats onto the one
    /// unresolved row carrying that key (<c>occurrence_count</c>++, content refreshed) and opens a fresh
    /// row once that one is resolved; a null key always inserts a fresh row. A null <paramref name="channelName"/> routes to the
    /// configured <c>default</c> channel. Writing the row is independent of delivery. Title and message
    /// are capped to column width at the persistence boundary; <paramref name="ct"/> is linked with
    /// the per-attempt token.
    /// </summary>
    public async Task AlertAsync(
        string title,
        string message,
        AlertSeverityCode severityCode,
        string? channelName = null,
        string? deduplicationKey = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (channelName is not null)
        {
            channelName = IdentifierSyntax.CanonicalizeKebab(channelName, nameof(channelName), IdentifierSyntax.ExtendedMaxLength);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await RaiseAlertCoreAsync(severityCode, title, message, channelName, deduplicationKey, linked.Token);
    }

    /// <summary>
    /// Subclass sink: persist the alert. Bounded text fields are truncated to their column widths and
    /// the deduplication key is normalized in the concrete implementation.
    /// </summary>
    protected abstract Task RaiseAlertCoreAsync(
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    );

    /// <summary>
    /// Writes an application-authored note onto this job's timeline: what the handler decided and
    /// why, recorded beside the framework's own events instead of in a log sink that is not joined to
    /// the job row.
    /// </summary>
    /// <remarks>
    /// Annotation, not logging: <c>events</c> is indexed for timeline queries, so use <c>ILogger</c>
    /// for volume. Notes ignore <c>AuditLevel</c>, which is a volume control over events Acta chooses
    /// to record; dropping an explicit call would be data loss, not filtering.
    /// </remarks>
    // Notes are the only event code an application can write, and the runtime never emits it. That is
    // what keeps every other event provably system-written: an application can annotate the ledger,
    // but it can never forge job.execution-finished. Do not widen this to a second code.
    //
    // Accepted consequence of ignoring AuditLevel: it no longer bounds a job's total event volume.
    // That is the caller's decision to make, and a silent drop would be the worse failure.
    public async Task NoteAsync(string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await WriteNoteCoreAsync<object>(message, null, linked.Token);
    }

    /// <inheritdoc cref="NoteAsync(string, CancellationToken)"/>
    /// <param name="message">The note line; truncated to the event message column width.</param>
    /// <param name="detail">
    /// Structured context, stored as JSON. Bounded by <c>JobsOptions.MaxInlinePayloadBytes</c>, the
    /// same ceiling as every other caller-controlled write, and a payload past it throws
    /// <see cref="PayloadTooLargeException"/> rather than truncating or surfacing a driver error.
    /// </param>
    /// <param name="ct">Cancellation token, linked to the job's own.</param>
    public async Task NoteAsync<T>(string message, T detail, CancellationToken ct = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(detail);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await WriteNoteCoreAsync(message, detail, linked.Token);
    }

    /// <summary>
    /// Subclass sink: append the note as a <c>job.note-recorded</c> event. Generic so the subclass serializes
    /// through its payload serializer and source-generated JSON is honored under Native AOT. The
    /// message is truncated to the column width in the concrete implementation.
    /// </summary>
    protected abstract Task WriteNoteCoreAsync<T>(string message, T? detail, CancellationToken ct);
}
