namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Durable, replay-safe named wait: the first pass arms a timer and suspends the Job budget-neutrally; the replay
    /// after the due instant consumes it and returns. A zero delay returns at once; no worker is held meanwhile.
    /// </summary>
    /// <param name="name">Dotted-kebab wait name, unique per Job; identifies the timer across replays.</param>
    /// <param name="delay">Wait length from DB now; whole-second precision (sub-second rounds up).</param>
    /// <param name="reasonMessage">Operator-readable suspend reason.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task SleepAsync(string name, TimeSpan delay, string? reasonMessage = null, CancellationToken ct = default)
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
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
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SleepCoreAsync(name, delay: null, resumeAtUtc.UtcDateTime, reasonMessage, linked.Token);
    }

    /// <summary>
    /// Subclass sink: arms or consumes the named sleep timer. Exactly one of <paramref name="delay"/>
    /// and <paramref name="resumeAtUtc"/> is non-null. Returns normally to continue the handler; throws
    /// the framework suspend signal to re-arm the Job for a subsequent claim.
    /// </summary>
    private protected abstract Task SleepCoreAsync(
        string name,
        TimeSpan? delay,
        DateTime? resumeAtUtc,
        string? reasonMessage,
        CancellationToken ct
    );

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
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
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
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, linked.Token);
        return outcome.ValueFormatId == 0 ? default : DeserializeSignalPayload<T>(outcome.ValueFormatId, outcome.Value!);
    }

    /// <summary>
    /// Bounded twin of <see cref="WaitSignalAsync(string, CancellationToken)"/>. The first pass stores
    /// <c>db_now + timeout</c> (whole seconds, rounded up, positive) as the slot's expiration; a replay reuses that
    /// instant and never extends it, so restarts cannot lengthen the wait. A raise landing before the replayed wait
    /// settles the slot is still taken; once it settles <c>Expired</c>, Acta ends the Job <c>Cancelled</c> with reason
    /// <c>job.wait-timed-out</c> and this call does not return. Use
    /// <see cref="TryWaitSignalAsync(string, TimeSpan, CancellationToken)"/> to resume the handler instead.
    /// </summary>
    public async Task WaitSignalAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
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
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, resumeOnTimeout: false, linked.Token);
        return outcome.ValueFormatId == 0 ? default : DeserializeSignalPayload<T>(outcome.ValueFormatId, outcome.Value!);
    }

    /// <summary>
    /// Resuming twin of <see cref="WaitSignalAsync(string, TimeSpan, CancellationToken)"/>: a wait whose
    /// re-entry settles the slot <c>Expired</c> returns a result carrying
    /// <see cref="SignalWaitOutcome.TimedOut"/>, and the handler continues rather than the Job being
    /// cancelled. The expiration, its never-extend replay rule, and a raise winning right up to that
    /// settlement are identical; only what an expired slot does to the handler differs.
    /// </summary>
    public async Task<SignalWaitOutcome> TryWaitSignalAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, resumeOnTimeout: true, linked.Token);
        return outcome.TimedOut ? SignalWaitOutcome.Expired : SignalWaitOutcome.Signalled;
    }

    /// <summary>
    /// Typed-payload variant of <see cref="TryWaitSignalAsync(string, TimeSpan, CancellationToken)"/>.
    /// <c>Value</c> is <c>default</c> on a timeout and on a presence-only raise; branch on
    /// <c>TimedOut</c> to tell the two apart.
    /// </summary>
    public async Task<SignalWaitOutcome<T>> TryWaitSignalAsync<T>(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, resumeOnTimeout: true, linked.Token);
        if (outcome.TimedOut)
        {
            return SignalWaitOutcome<T>.Expired;
        }

        var value = outcome.ValueFormatId == 0 ? default : DeserializeSignalPayload<T>(outcome.ValueFormatId, outcome.Value!);
        return SignalWaitOutcome<T>.Signalled(value);
    }

    /// <summary>
    /// Rejects a non-positive timeout before any store call: a zero or negative expiration would
    /// arm a slot that is due the instant it is written. Both non-positive cases are rejected here
    /// rather than delegating the negative one, because DurationSyntax phrases it as a delay
    /// ("Delay must not be negative"), which is the wrong noun for a wait. What survives the check
    /// rounds like SleepAsync.
    /// </summary>
    private static int ToWaitTimeoutSeconds(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Wait timeout must be positive.")
            : DurationSyntax.ToWholeSeconds(timeout, nameof(timeout));

    /// <summary>
    /// The raised slot's stored payload: format id + bytes. <c>ValueFormatId == 0</c> means a
    /// presence-only signal with no payload (<see cref="Value"/> is <c>null</c>). <c>TimedOut</c> is
    /// set only on the resuming path of a bounded wait whose expiration passed.
    /// </summary>
    private protected readonly record struct SignalSlotState(byte ValueFormatId, byte[]? Value, bool TimedOut = false);

    /// <summary>
    /// Subclass sink: reads or arms the named signal slot. Returns normally with the slot payload when
    /// it is <c>Set</c>; throws the framework signal-suspend signal to re-arm the Job for a subsequent claim.
    /// </summary>
    private protected abstract Task<SignalSlotState> WaitSignalCoreAsync(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink for the bounded overloads: <paramref name="timeoutSeconds"/> is the wait length the
    /// store resolves against the DB clock into the slot's absolute expiration, written only when the
    /// slot is first armed (never on re-entry, and null for an unbounded wait). On an expired slot the
    /// implementation returns a timed-out outcome when <paramref name="resumeOnTimeout"/> is set, and
    /// otherwise ends the attempt. The default forwards to the unbounded sink, so a subclass that
    /// overrides only that one keeps working and simply never expires a wait.
    /// </summary>
    private protected virtual Task<SignalSlotState> WaitSignalCoreAsync(
        string name,
        int? timeoutSeconds,
        bool resumeOnTimeout,
        CancellationToken ct
    ) => WaitSignalCoreAsync(name, ct);

    /// <summary>
    /// Subclass sink for a wait bounded by an instant, a group's stored deadline, rather than a length: the store arms
    /// that instant as the slot's expiration, a slot first armed at or past it times out at once, and a timeout
    /// resumes the handler. The default passes the whole seconds left on the host clock, at least one, to the
    /// length-bounded sink, so a subclass without durable storage keeps working.
    /// </summary>
    private protected virtual Task<SignalSlotState> WaitSignalUntilCoreAsync(string name, DateTime dueAtUtc, CancellationToken ct) =>
        WaitSignalCoreAsync(name, (int)Math.Max(1, Math.Ceiling((dueAtUtc - DateTime.UtcNow).TotalSeconds)), resumeOnTimeout: true, ct);

    /// <summary>
    /// Subclass sink: deserializes a raised signal payload via the runtime serializer registry.
    /// </summary>
    private protected abstract T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value);
}
