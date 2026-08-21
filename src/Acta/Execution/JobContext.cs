using System.Diagnostics;
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
    /// Bounded twin of <see cref="WaitSignalAsync(string, CancellationToken)"/>. The first pass stores
    /// <c>db_now + timeout</c> as the slot's absolute expiration; a replay reuses that instant and never
    /// extends it, so restarts and worker downtime cannot lengthen the wait. Passing that instant makes
    /// the wait resolvable as timed out rather than closing the slot to a raise: it wakes the Suspended
    /// Job, and the replayed wait settles the slot under its lock, so a raise landing before that
    /// re-entry is still taken. Once the re-entry settles the slot <c>Expired</c>, Acta terminates the
    /// Job <c>Cancelled</c> with reason <c>job.wait-timed-out</c> and this call does not return; use
    /// <see cref="TryWaitSignalAsync(string, TimeSpan, CancellationToken)"/> to resume the handler
    /// instead. The timeout is budget-neutral, and cancelling <paramref name="ct"/> stays a separate,
    /// non-durable concern.
    /// </summary>
    /// <param name="name">Kebab-case signal name, unique per Job; identifies the slot across replays.</param>
    /// <param name="timeout">Wait length from DB now; whole-second precision (sub-second rounds up). Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task WaitSignalAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await WaitSignalCoreAsync(name, seconds, resumeOnTimeout: false, linked.Token);
    }

    /// <summary>
    /// Typed-payload variant of <see cref="WaitSignalAsync(string, TimeSpan, CancellationToken)"/>. A
    /// presence-only signal (raised without a value) returns <c>default</c>; a timeout does not return.
    /// </summary>
    public async Task<T?> WaitSignalAsync<T>(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, resumeOnTimeout: false, linked.Token);
        return outcome.ValueFormatId == 0 ? default : DeserializeSignalPayload<T>(outcome.ValueFormatId, outcome.Value!);
    }

    /// <summary>
    /// Resuming twin of <see cref="WaitSignalAsync(string, TimeSpan, CancellationToken)"/>: a wait whose
    /// re-entry settles the slot <c>Expired</c> returns a result carrying
    /// <see cref="SignalWaitResult.TimedOut"/>, and the handler continues rather than the Job being
    /// cancelled. The expiration, its never-extend replay rule, and a raise winning right up to that
    /// settlement are identical; only what an expired slot does to the handler differs.
    /// </summary>
    public async Task<SignalWaitResult> TryWaitSignalAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, resumeOnTimeout: true, linked.Token);
        return outcome.TimedOut ? SignalWaitResult.Expired : SignalWaitResult.Signalled;
    }

    /// <summary>
    /// Typed-payload variant of <see cref="TryWaitSignalAsync(string, TimeSpan, CancellationToken)"/>.
    /// <c>Value</c> is <c>default</c> on a timeout and on a presence-only raise; branch on
    /// <c>TimedOut</c> to tell the two apart.
    /// </summary>
    public async Task<SignalWaitResult<T>> TryWaitSignalAsync<T>(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, resumeOnTimeout: true, linked.Token);
        if (outcome.TimedOut)
        {
            return SignalWaitResult<T>.Expired;
        }

        var value = outcome.ValueFormatId == 0 ? default : DeserializeSignalPayload<T>(outcome.ValueFormatId, outcome.Value!);
        return SignalWaitResult<T>.Signalled(value);
    }

    // Rejects a non-positive timeout before any store call: a zero or negative expiration would arm a
    // slot that is due the instant it is written. Both non-positive cases are rejected here rather than
    // delegating the negative one, because DurationSyntax phrases it as a delay ("Delay must not be
    // negative"), which is the wrong noun for a wait. What survives the check rounds like SleepAsync.
    private static int ToWaitTimeoutSeconds(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Wait timeout must be positive.")
            : DurationSyntax.ToWholeSeconds(timeout, nameof(timeout));

    /// <summary>
    /// The raised slot's stored payload: format id + bytes. <c>ValueFormatId == 0</c> means a
    /// presence-only signal with no payload (<see cref="Value"/> is <c>null</c>). <c>TimedOut</c> is
    /// set only on the resuming path of a bounded wait whose expiration passed.
    /// </summary>
    protected readonly record struct SignalWaitOutcome(byte ValueFormatId, byte[]? Value, bool TimedOut = false);

    /// <summary>
    /// Subclass sink: reads or arms the named signal slot. Returns normally with the slot payload when
    /// it is <c>Set</c>; throws the framework signal-suspend signal to re-arm the Job for a subsequent claim.
    /// </summary>
    protected abstract Task<SignalWaitOutcome> WaitSignalCoreAsync(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink for the bounded overloads: <paramref name="timeoutSeconds"/> is the wait length the
    /// store resolves against the DB clock into the slot's absolute expiration, written only when the
    /// slot is first armed (never on re-entry, and null for an unbounded wait). On an expired slot the
    /// implementation returns a timed-out outcome when <paramref name="resumeOnTimeout"/> is set, and
    /// otherwise ends the attempt. The default forwards to the unbounded sink, so a subclass that
    /// overrides only that one keeps working and simply never expires a wait.
    /// </summary>
    protected virtual Task<SignalWaitOutcome> WaitSignalCoreAsync(
        string name,
        int? timeoutSeconds,
        bool resumeOnTimeout,
        CancellationToken ct
    ) => WaitSignalCoreAsync(name, ct);

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
    /// Convenience overload of
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<JobEnqueueOutcome> StartChildAsync<TInput>(string name, TInput input, CancellationToken ct)
        where TInput : notnull => StartChildAsync(name, input, configure: null, ct: ct);

    /// <summary>
    /// Raw-route variant of
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    /// for explicit
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
    /// Durable, replay-safe wait for a child started by
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> to reach
    /// a terminal status. Returns the child's terminal outcome immediately when it already finished;
    /// otherwise suspends this Job (budget-neutral) until the child's terminal landing releases it.
    /// Never throws on a failed or cancelled child; branch on <see cref="ChildJobOutcome.Succeeded"/>.
    /// The outcome latch lives on this Job and survives the child row's retention purge.
    /// </summary>
    public async Task<ChildJobOutcome> WaitChildAsync(long childJobId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(ChildLatchName(childJobId), linked.Token);
        return ParseChildOutcome(childJobId, outcome);
    }

    /// <summary>
    /// Bounded twin of <see cref="WaitChildAsync"/>. The first pass stores <c>db_now + timeout</c> as
    /// the latch's absolute expiration; a replay reuses that instant and never extends it, so restarts
    /// and worker downtime cannot lengthen the wait. Passing that instant makes the wait resolvable as
    /// timed out rather than closing the latch: the replayed wait settles it under its lock, so a child
    /// landing before that re-entry is still taken. A child timeout never cancels this Job: the
    /// handler resumes with <see cref="ChildWaitResult.TimedOut"/> and is free to compensate, start a
    /// replacement child, or cancel itself. Acta cancels the unfinished child and its descendant
    /// subtree (reason <c>job.wait-timed-out</c>) before this call returns. Waiting stays
    /// budget-neutral, and cancelling <paramref name="ct"/> remains a separate, non-durable concern.
    /// There is deliberately no non-Try twin: a child timeout resolves in the parent's favour, so
    /// there is nothing for a non-returning overload to mean.
    /// </summary>
    /// <param name="childJobId">The child started by <c>StartChildAsync</c>.</param>
    /// <param name="timeout">Wait length from DB now; whole-second precision (sub-second rounds up). Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task<ChildWaitResult> TryWaitChildAsync(long childJobId, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(ChildLatchName(childJobId), seconds, resumeOnTimeout: true, linked.Token);
        if (!outcome.TimedOut)
        {
            return ChildWaitResult.Landed(ParseChildOutcome(childJobId, outcome));
        }

        // The parent has stopped waiting, so the child's work is no longer wanted. Cancelling before
        // the result is handed back means the handler never sees TimedOut while the subtree is still
        // burning workers, and a replay re-runs a cancel that is a no-op on terminal rows.
        await CancelTimedOutChildCoreAsync(childJobId, linked.Token);
        return ChildWaitResult.Expired(childJobId);
    }

    // Read side of the child-latch checkpoint key. The name is persisted in the ledger and matched as
    // text against the one RaiseChildLatch writes, in another process and possibly another culture, so
    // the two renderings must agree byte for byte; the invariant culture is stated on both sides rather
    // than inherited from whatever the ambient one happens to be.
    private static string ChildLatchName(long childJobId) => ChildSignalPrefix + childJobId.ToString(CultureInfo.InvariantCulture);

    private static ChildJobOutcome ParseChildOutcome(long childJobId, SignalWaitOutcome outcome) =>
        outcome.Value is null
            ? throw new InvalidOperationException($"Child outcome slot for job {childJobId} carries no envelope.")
            : ChildOutcomeEnvelope.Parse(outcome.Value);

    /// <summary>
    /// Subclass sink: cancel the timed-out <paramref name="childJobId"/> and its non-terminal
    /// descendant subtree, so a wait the parent abandoned does not leave orphaned work running. Must be
    /// idempotent: a replayed handler re-enters the expired wait and calls this again, and a cancel of
    /// an already-terminal job is a no-op. The default does nothing, so a <see cref="JobContext"/>
    /// subclass that only records calls keeps working.
    /// </summary>
    protected virtual Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct) => Task.CompletedTask;

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
    /// Bounded twin of <see cref="WaitChildrenAsync(IReadOnlyList{long}, CancellationToken)"/>, and the
    /// same call as <see cref="TryWaitChildrenAsync"/>. It returns a
    /// <see cref="ChildrenWaitResult"/> rather than the unbounded form's plain list, because a group
    /// that ran out of time has more to report than an outcome per child.
    /// </summary>
    /// <param name="childJobIds">The children to wait for, in the order the outcomes come back.</param>
    /// <param name="timeout">Budget for the whole group from DB now, not per child. Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<ChildrenWaitResult> WaitChildrenAsync(IReadOnlyList<long> childJobIds, TimeSpan timeout, CancellationToken ct = default) =>
        TryWaitChildrenAsync(childJobIds, timeout, ct);

    /// <summary>
    /// Durable, replay-safe bounded wait for a whole group of children. The first pass stores
    /// <c>db_now + timeout</c> once as the group's absolute deadline; every child in the group, on this
    /// pass and on every replay, waits toward that one instant, so the budget cannot restart per child
    /// or per replay. Children that landed before it keep their terminal outcome. On expiry Acta
    /// cancels only the unfinished children and their descendant subtrees (reason
    /// <c>job.wait-timed-out</c>); the awaiting Job is never cancelled and resumes with the result.
    /// There is deliberately no non-Try twin, for the reason <see cref="TryWaitChildAsync"/> has none.
    /// Waiting stays budget-neutral, and cancelling <paramref name="ct"/> stays a non-durable concern.
    /// <see cref="ResetStateAsync"/> is the one exit from the never-restart rule: it clears the stored
    /// deadline with every other checkpoint, so a group re-entered after a reset starts a fresh one.
    /// </summary>
    /// <param name="childJobIds">The children to wait for, in the order the outcomes come back.</param>
    /// <param name="timeout">Budget for the whole group from DB now, not per child. Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task<ChildrenWaitResult> TryWaitChildrenAsync(
        IReadOnlyList<long> childJobIds,
        TimeSpan timeout,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(childJobIds);
        // Every argument is checked before the first store call, and a bad id anywhere in the list stops
        // the whole call, so a rejected group never leaves a deadline slot or a half-armed latch behind.
        ToWaitTimeoutSeconds(timeout);
        for (var i = 0; i < childJobIds.Count; i++)
        {
            if (childJobIds[i] <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(childJobIds), childJobIds[i], "Child job ids must be positive.");
            }
        }

        if (childJobIds.Count == 0)
        {
            // No group, so no deadline to persist: an empty wait is over before it starts.
            return ChildrenWaitResult.From([]);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return ChildrenWaitResult.From(await WaitChildGroupAsync(childJobIds, timeout, linked.Token));
    }

    // The one place the group deadline rule lives; every bounded group API funnels through it, and a
    // null timeout is the unbounded loop these wrappers have always run.
    //
    // At most one child arms per pass, because the first wait that cannot resolve suspends the attempt.
    // Reaching it still costs real time: every member ahead of it that resolves from its own latch is a
    // store round trip. So the pass reads the DB clock once, anchors a monotonic stopwatch to that
    // reading, and carries it forward, giving each iteration a remaining measured at the moment it runs
    // rather than one measured before the walk began. Without that, the arming member would have
    // inherited the whole walk as deadline overshoot. Mirrors the alerting pass's settlement clock, and
    // costs no extra clock round trip.
    //
    // Each arm rounds the remaining time DOWN to whole seconds; a slot armed on an earlier pass keeps
    // its own due even once the remaining time has shrunk, and that is harmless because both were
    // derived from the same fixed instant.
    //
    // A slot's due therefore lands within one second before the group deadline, plus the arm's own
    // store round trip, which stamps the due against a clock that has moved on since the remaining was
    // measured. That residual is one round trip, the same on every arm, and it never accumulates,
    // because every arm measures from the deadline rather than from the previous arm. Removing it
    // outright would need the arm to take an absolute instant instead of a duration.
    private async Task<IReadOnlyList<ChildJobOutcome>> WaitChildGroupAsync(
        IReadOnlyList<long> childJobIds,
        TimeSpan? timeout,
        CancellationToken ct
    )
    {
        if (timeout is not { } bound)
        {
            return await WaitChildrenAsync(childJobIds, ct);
        }

        var deadline = await GetOrSetWaitDeadlineCoreAsync(GroupDeadlineName(childJobIds), bound, ct);
        var passStarted = Stopwatch.GetTimestamp();

        var outcomes = new ChildJobOutcome[childJobIds.Count];
        for (var i = 0; i < childJobIds.Count; i++)
        {
            var remaining = RemainingWait(deadline.DeadlineAtUtc, deadline.NowUtc, Stopwatch.GetElapsedTime(passStarted));
            var result = await TryWaitChildAsync(childJobIds[i], remaining, ct);
            outcomes[i] = result.Outcome ?? ChildJobOutcome.Expired(childJobIds[i]);
        }
        return outcomes;
    }

    // The whole seconds left until the group deadline, floored, with a floor of one. The anchor is the
    // pass's one DB clock reading advanced by the monotonic time the pass has spent since, so the
    // walk over already-resolved members is spent out of the group's budget rather than added to it.
    //
    // A group deadline is a NOT-BEFORE, not a not-after: a member does not give up before the instant,
    // and the store may stamp its due a round trip past it because the arm reads the clock again. That
    // trailing round trip is deliberate and absorbed by those semantics; flooring is what keeps it to a
    // round trip instead of a whole extra second.
    //
    // A wait must also carry a positive bound, so an already-passed deadline arms one second rather
    // than zero. Accepted consequence: a child whose latch does not exist yet at that point suspends
    // once before it can expire, because wait_signal resolves only a wait an earlier call armed. That
    // costs one extra second-long tick per unfinished child, and the alternative is a special case
    // inside the arbiter that every other wait would have to reason about.
    //
    // Internal, and over bare instants rather than the protected WaitDeadline, so the arithmetic can be
    // pinned directly: Stopwatch is a static monotonic source with no seam, so a unit fact cannot make
    // a pass take measurable time.
    internal static TimeSpan RemainingWait(DateTime deadlineAtUtc, DateTime passNowUtc, TimeSpan elapsed)
    {
        var remaining = deadlineAtUtc - (passNowUtc + elapsed);
        var seconds = remaining.Ticks <= TimeSpan.TicksPerSecond ? 1L : remaining.Ticks / TimeSpan.TicksPerSecond;
        return TimeSpan.FromSeconds(seconds);
    }

    // Reserved deadline-slot name, derived from the group's identity the way MapAsync derives a child
    // name from an item key: the child ids hashed to a stable tail. The ids come back identical on
    // every replay (a child start dedupes onto the same row), so the name is stable, and the sys.
    // prefix is rejected for user variable names, so it cannot collide with one. Two waits on the same
    // children in the same Job are the same group and deliberately share the deadline.
    //
    // Sorted first, so the name is a property of the SET of children rather than of the order the
    // caller happened to list them in. A handler that reorders the same ids between replays would
    // otherwise mint a second slot and hand the group a fresh budget, which would make never-restart a
    // promise about caller discipline instead of a structural one. Caller order is not lost: the
    // outcome array is built in the order the ids were given.
    private static string GroupDeadlineName(IReadOnlyList<long> childJobIds)
    {
        var ordered = new long[childJobIds.Count];
        for (var i = 0; i < childJobIds.Count; i++)
        {
            ordered[i] = childJobIds[i];
        }
        Array.Sort(ordered);

        var canonical = new StringBuilder();
        foreach (var id in ordered)
        {
            canonical.Append(id.ToString(CultureInfo.InvariantCulture)).Append('.');
        }
        return GroupDeadlinePrefix + ShortHash(canonical.ToString());
    }

    // Internal rather than private so the test host can find the slot it has to rewind to stage a group
    // expiry, off the one name that writes it.
    internal const string GroupDeadlinePrefix = "sys.wait-group.";

    // A wrapper starts every child before it waits on any of them, so a rejected timeout has to throw
    // ahead of the first enqueue rather than when the wait finally reaches it. A null timeout is the
    // unbounded overload and has nothing to check.
    private static void ValidateGroupTimeout(TimeSpan? timeout)
    {
        if (timeout is { } bound)
        {
            ToWaitTimeoutSeconds(bound);
        }
    }

    /// <summary>
    /// A bounded group wait's fixed end instant plus the clock reading it was measured against, so the
    /// caller can derive the remaining time without a second round trip.
    /// </summary>
    protected readonly record struct WaitDeadline(DateTime DeadlineAtUtc, DateTime NowUtc);

    /// <summary>
    /// Subclass sink: read the group's stored absolute deadline, writing <c>db_now + timeout</c> on the
    /// first call and returning the stored instant unchanged on every call after it (first-write-wins,
    /// the <see cref="GetOrSetVariableAsync{T}(string, Func{T}, CancellationToken)"/> shape). The
    /// returned <c>NowUtc</c> is the same clock reading the write used. The default computes an instant
    /// from the host clock and stores nothing, so a <see cref="JobContext"/> subclass without durable
    /// storage keeps working; only a durable implementation makes the deadline survive a replay.
    /// </summary>
    protected virtual Task<WaitDeadline> GetOrSetWaitDeadlineCoreAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var nowUtc = DateTime.UtcNow;
        return Task.FromResult(new WaitDeadline(nowUtc + timeout, nowUtc));
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
    /// every child first and join with
    /// <see cref="WaitChildrenAsync(IReadOnlyList{long}, CancellationToken)"/>.
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
        return ToJobOutcome(child.JobId, outcome.Status);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome> ExecuteChildAsync<TInput>(string name, TInput input, CancellationToken ct)
        where TInput : notnull => ExecuteChildAsync(name, input, configure: null, ct: ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// the child is started, then awaited under one stored absolute expiration that a replay reuses and
    /// never extends. On expiry the outcome reports <see cref="JobOutcome.IsTimedOut"/>, Acta cancels
    /// the child and its descendant subtree, and this Job resumes.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="timeout">Wait length from DB now; whole-second precision (sub-second rounds up). Must be positive.</param>
    /// <param name="configure">Optional enqueue options; the parent id and deduplication key are framework-set and win over configured values.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task<JobOutcome> ExecuteChildAsync<TInput>(
        string name,
        TInput input,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        // Checked before the child is started, not when the wait reaches it: a rejected timeout must not
        // leave an enqueued child behind that nothing is going to wait for.
        ToWaitTimeoutSeconds(timeout);
        var child = await StartChildAsync(name, input, configure, ct);
        // One child needs no group deadline slot: the latch's own stored expiration already is the one
        // persisted absolute instant, and a second copy of it could only drift.
        var result = await TryWaitChildAsync(child.JobId, timeout, ct);
        return result.Outcome is { } outcome
            ? ToJobOutcome(child.JobId, outcome.Status)
            : JobOutcome.TimedOut(child.JobId, TimedOutChildStatus);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the fourth positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome> ExecuteChildAsync<TInput>(string name, TInput input, TimeSpan timeout, CancellationToken ct)
        where TInput : notnull => ExecuteChildAsync(name, input, timeout, configure: null, ct: ct);

    private static JobOutcome ToJobOutcome(long childJobId, JobStatusCode status) =>
        status switch
        {
            JobStatusCode.Succeeded => JobOutcome.Succeeded(childJobId),
            JobStatusCode.Cancelled => JobOutcome.Cancelled(childJobId),
            _ => JobOutcome.Failed(childJobId),
        };

    // What a timed-out wait leaves the child in: Acta cancels it before the handler resumes, so the
    // outcome reports Cancelled beside the timeout flag rather than inventing a status of its own.
    private const JobStatusCode TimedOutChildStatus = JobStatusCode.Cancelled;

    /// <summary>
    /// Result-returning variant of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// on a successful child the
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

        return JobOutcome<TResult>.Succeeded(child.JobId, await RequireChildResultAsync<TResult>(child.JobId, name, ct));
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(string name, TInput input, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => ExecuteChildAsync<TInput, TResult>(name, input, configure: null, ct: ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// the child is started, then awaited under one stored absolute expiration that a replay reuses and
    /// never extends. On expiry the outcome reports <see cref="JobOutcome.IsTimedOut"/> with no value,
    /// Acta cancels the child and its descendant subtree, and this Job resumes.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="timeout">Wait length from DB now; whole-second precision (sub-second rounds up). Must be positive.</param>
    /// <param name="configure">Optional enqueue options; the parent id and deduplication key are framework-set and win over configured values.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(
        string name,
        TInput input,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ToWaitTimeoutSeconds(timeout);
        var child = await StartChildAsync(name, input, configure, ct);
        var result = await TryWaitChildAsync(child.JobId, timeout, ct);
        if (result.Outcome is not { } outcome)
        {
            return JobOutcome<TResult>.TimedOut(child.JobId, TimedOutChildStatus);
        }
        if (outcome.Status != JobStatusCode.Succeeded)
        {
            return outcome.Status == JobStatusCode.Cancelled
                ? JobOutcome<TResult>.Cancelled(child.JobId)
                : JobOutcome<TResult>.Failed(child.JobId);
        }

        return JobOutcome<TResult>.Succeeded(child.JobId, await RequireChildResultAsync<TResult>(child.JobId, name, ct));
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the fourth positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(string name, TInput input, TimeSpan timeout, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => ExecuteChildAsync<TInput, TResult>(name, input, timeout, configure: null, ct: ct);

    private async Task<TResult> RequireChildResultAsync<TResult>(long childJobId, string name, CancellationToken ct)
        where TResult : notnull =>
        await GetChildResultAsync<TResult>(childJobId, ct)
        ?? throw new InvalidOperationException(
            $"Child job {childJobId} ('{name}') succeeded but stored no result; "
                + "use the non-result ExecuteChildAsync overload for result-less children."
        );

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
    /// order. A nicer wrapper over
    /// <see cref="WaitChildrenAsync(IReadOnlyList{long}, CancellationToken)"/>: it does not throw because a child
    /// failed and does not cancel siblings; branch on the returned outcome or call
    /// <see cref="JoinOutcome.ThrowIfAnyFailed"/>.
    /// </summary>
    public async Task<JoinOutcome> JoinAsync(IReadOnlyList<JobEnqueueOutcome> children, CancellationToken ct = default) =>
        new(await WaitChildrenAsync(ChildIds(children), ct));

    /// <summary>
    /// Bounded twin of <see cref="JoinAsync(IReadOnlyList{JobEnqueueOutcome}, CancellationToken)"/>: the
    /// whole join shares one stored absolute deadline that a replay reuses and never extends. It returns
    /// a <see cref="ChildrenWaitResult"/> rather than a <see cref="JoinOutcome"/>, because the two carry
    /// the same ordered child outcomes and only one of them can also say the group ran out of time;
    /// minting a second near-identical record would have been the larger surface. On expiry Acta cancels
    /// only the unfinished children and their subtrees, and this Job resumes.
    /// </summary>
    /// <param name="children">The child handles to join on, in the order the outcomes come back.</param>
    /// <param name="timeout">Budget for the whole join from DB now, not per child. Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<ChildrenWaitResult> JoinAsync(
        IReadOnlyList<JobEnqueueOutcome> children,
        TimeSpan timeout,
        CancellationToken ct = default
    ) => TryWaitChildrenAsync(ChildIds(children), timeout, ct);

    private static long[] ChildIds(IReadOnlyList<JobEnqueueOutcome> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var ids = new long[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            ids[i] = children[i].JobId;
        }
        return ids;
    }

    /// <summary>
    /// Starts each named branch as a child job and waits for all of them, returning branch-keyed
    /// outcomes. The group and branch names are validated and branch names must be unique; every
    /// branch is started before any is awaited. Does not throw on a failed branch and does not
    /// fail-fast; branch on the outcome or call <see cref="ParallelOutcome.ThrowIfAnyFailed"/>.
    /// </summary>
    public Task<ParallelOutcome> ParallelAsync(string groupName, Action<ParallelBuilder> configure, CancellationToken ct = default) =>
        ParallelCoreAsync(groupName, configure, timeout: null, ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="ParallelAsync(string, Action{ParallelBuilder}, CancellationToken)"/>: every branch
    /// waits toward one stored absolute deadline that a replay reuses and never extends. A branch that
    /// did not land by then reports <see cref="ChildJobOutcome.TimedOut"/> and is cancelled along with
    /// its subtree; the branch keying is unchanged, and this Job resumes either way.
    /// </summary>
    /// <param name="groupName">Kebab-case group name; each branch child is named <c>groupName-branchName</c>.</param>
    /// <param name="configure">Branch builder; every branch is started before any is awaited.</param>
    /// <param name="timeout">Budget for the whole group from DB now, not per branch. Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<ParallelOutcome> ParallelAsync(
        string groupName,
        Action<ParallelBuilder> configure,
        TimeSpan timeout,
        CancellationToken ct = default
    ) => ParallelCoreAsync(groupName, configure, timeout, ct);

    private async Task<ParallelOutcome> ParallelCoreAsync(
        string groupName,
        Action<ParallelBuilder> configure,
        TimeSpan? timeout,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(configure);
        IdentifierSyntax.ValidateUserKebab(groupName, nameof(groupName), IdentifierSyntax.ExtendedMaxLength);
        ValidateGroupTimeout(timeout);

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

        var outcomes = await WaitChildGroupAsync(ids, timeout, ct);

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
    public Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull => MapCoreAsync<TItem, TKey, TInput>(groupName, items, itemKey, child, timeout: null, ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="MapAsync{TItem, TKey, TInput}(string, IEnumerable{TItem}, Func{TItem, TKey}, Func{TItem, TInput}, CancellationToken)"/>:
    /// every fanned-out child waits toward one stored absolute deadline that a replay reuses and never
    /// extends. An item whose child did not land by then reports
    /// <see cref="ChildJobOutcome.TimedOut"/> and that child is cancelled along with its subtree; the
    /// item keying is unchanged, and this Job resumes either way.
    /// </summary>
    /// <param name="groupName">Kebab-case group name; the group plus item key derives each child name.</param>
    /// <param name="items">The items to fan out over, one child each.</param>
    /// <param name="itemKey">Stable per-item key; duplicates are rejected before any child is started.</param>
    /// <param name="child">Maps an item to the typed child input.</param>
    /// <param name="timeout">Budget for the whole group from DB now, not per item. Must be positive.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<MapOutcome<TKey>> MapAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        TimeSpan timeout,
        CancellationToken ct = default
    )
        where TKey : notnull
        where TInput : notnull => MapCoreAsync<TItem, TKey, TInput>(groupName, items, itemKey, child, timeout, ct);

    private async Task<MapOutcome<TKey>> MapCoreAsync<TItem, TKey, TInput>(
        string groupName,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TItem, TInput> child,
        TimeSpan? timeout,
        CancellationToken ct
    )
        where TKey : notnull
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(itemKey);
        ArgumentNullException.ThrowIfNull(child);
        IdentifierSyntax.ValidateUserKebab(groupName, nameof(groupName), IdentifierSyntax.ExtendedMaxLength);
        ValidateGroupTimeout(timeout);

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

        var outcomes = await WaitChildGroupAsync(ids, timeout, ct);

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
    /// Convenience overload of
    /// <see cref="RunStepAsync(string, Func{CancellationToken, Task}, Action{StepOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    /// <param name="name">Stable kebab-case slot name, unique per Job; durable identity across replays.</param>
    /// <param name="body">The side-effecting work; receives a cancellation token linked to the attempt.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task RunStepAsync(string name, Func<CancellationToken, Task> body, CancellationToken ct) =>
        RunStepAsync(name, body, configure: null, ct: ct);

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

    /// <summary>
    /// Convenience overload of
    /// <see cref="RunStepAsync{TResult}(string, Func{CancellationToken, Task{TResult}}, Action{StepOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<TResult> RunStepAsync<TResult>(string name, Func<CancellationToken, Task<TResult>> body, CancellationToken ct) =>
        RunStepAsync(name, body, configure: null, ct: ct);

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
