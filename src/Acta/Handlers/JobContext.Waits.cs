using System.Diagnostics;

namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Durable, replay-safe named wait: the first pass arms a timer and suspends the Job budget-neutrally; the replay
    /// after the due instant consumes it and returns; no worker is held meanwhile.
    /// </summary>
    /// <param name="name">Dotted-kebab wait name, unique per Job; identifies the timer across replays.</param>
    /// <param name="delay">Wait length from DB now, sub-second rounding up; zero or less arms nothing, and an armed timer still decides.</param>
    /// <param name="reasonMessage">Operator-readable suspend reason.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task SleepAsync(string name, TimeSpan delay, string? reasonMessage = null, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(SleepAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        var seconds = delay <= TimeSpan.Zero ? 0 : DurationSyntax.ToWholeSeconds(delay, nameof(delay));
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
        ThrowIfInAtMostOnceStep(nameof(SleepUntilAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
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
    /// already <c>Set</c>; otherwise arms a <c>Pending</c> checkpoint and suspends the Job until an external
    /// <c>IJobs.RaiseSignalAsync</c> raises it. Replay re-runs the handler from the start; on the replay
    /// the wait returns because the signal is now <c>Set</c>. Suspending is budget-neutral.
    /// </summary>
    /// <param name="name">Kebab-case signal name, unique per Job; identifies the signal across replays.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task WaitSignalAsync(string name, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitSignalAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        ThrowIfTimedOut(await WaitSignalCoreAsync(name, linked.Token), name);
    }

    /// <summary>
    /// Typed-payload variant of <see cref="WaitSignalAsync(string, CancellationToken)"/>. Deserializes the
    /// raised payload through the registered serializer. A presence-only signal (raised without a value)
    /// returns <c>default</c>.
    /// </summary>
    public async Task<T?> WaitSignalAsync<T>(string name, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitSignalAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = ThrowIfTimedOut(await WaitSignalCoreAsync(name, linked.Token), name);
        return ReadSignalValue<T>(name, outcome);
    }

    /// <summary>
    /// Bounded twin of <see cref="WaitSignalAsync(string, CancellationToken)"/>, answering whether the signal arrived. The
    /// first pass stores <c>db_now + timeout</c> (whole seconds, rounded up, at least one) as the wait's expiration, and a
    /// replay reuses that instant and never extends it. A raise landing before the replayed wait settles it is still
    /// taken; once it settles <c>Expired</c> the outcome reports <see cref="SignalWaitOutcome.TimedOut"/>, and
    /// <see cref="SignalWaitOutcome.ThrowIfTimedOut"/> fails the job with it.
    /// </summary>
    public async Task<SignalWaitOutcome> WaitSignalAsync(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitSignalAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, linked.Token);
        return outcome.TimedOut ? SignalWaitOutcome.Expired(name) : SignalWaitOutcome.Signalled(name);
    }

    /// <summary>
    /// Typed twin of <see cref="WaitSignalAsync(string, TimeSpan, CancellationToken)"/>: <c>Value</c> is the raised
    /// payload, <c>default</c> on a timeout and on a presence-only raise, and <see cref="SignalWaitOutcome{T}.ValueOrThrow"/>
    /// reads it or fails the job with the timeout.
    /// </summary>
    public async Task<SignalWaitOutcome<T>> WaitSignalAsync<T>(string name, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitSignalAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        var seconds = ToWaitTimeoutSeconds(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalCoreAsync(name, seconds, linked.Token);
        if (outcome.TimedOut)
        {
            return SignalWaitOutcome<T>.Expired(name);
        }

        return SignalWaitOutcome<T>.Signalled(name, ReadSignalValue<T>(name, outcome));
    }

    /// <summary>
    /// <see cref="WaitSignalAsync(string, TimeSpan, CancellationToken)"/> bounded by an instant rather than a length:
    /// <paramref name="until"/> is stored as the wait's expiration, and a wait first armed at or past it times out at
    /// once, so a deadline several waits share is the same instant at each, and a restart after it meets it again.
    /// </summary>
    public async Task<SignalWaitOutcome> WaitSignalAsync(string name, DateTimeOffset until, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitSignalAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalUntilCoreAsync(name, until.UtcDateTime, linked.Token);
        return outcome.TimedOut ? SignalWaitOutcome.Expired(name) : SignalWaitOutcome.Signalled(name);
    }

    /// <summary>
    /// Typed twin of <see cref="WaitSignalAsync(string, DateTimeOffset, CancellationToken)"/>, read as
    /// <see cref="WaitSignalAsync{T}(string, TimeSpan, CancellationToken)"/> reads its value.
    /// </summary>
    public async Task<SignalWaitOutcome<T>> WaitSignalAsync<T>(string name, DateTimeOffset until, CancellationToken ct = default)
    {
        ThrowIfInAtMostOnceStep(nameof(WaitSignalAsync));
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var outcome = await WaitSignalUntilCoreAsync(name, until.UtcDateTime, linked.Token);
        return outcome.TimedOut
            ? SignalWaitOutcome<T>.Expired(name)
            : SignalWaitOutcome<T>.Signalled(name, ReadSignalValue<T>(name, outcome));
    }

    /// <summary>A raised value read as <typeparamref name="T"/>; one it cannot read fails the job at once, as every attempt reads the same.</summary>
    [DebuggerDisableUserUnhandledExceptions]
    private T? ReadSignalValue<T>(string name, SignalCheckpointState outcome)
    {
        if (outcome.ValueFormatId == 0)
        {
            return default;
        }

        try
        {
            return DeserializeSignalPayload<T>(outcome.ValueFormatId, outcome.Value!);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SignalValueContractMismatchException(name, typeof(T), ex);
        }
    }

    /// <summary>
    /// An unbounded wait cannot report a timeout, so one that meets an expired checkpoint, left by a bound the code has
    /// since dropped, fails with it: a retry would meet the same expired wait.
    /// </summary>
    private static SignalCheckpointState ThrowIfTimedOut(SignalCheckpointState state, string name) =>
        state.TimedOut ? throw new SignalWaitTimeoutException(name) : state;

    /// <summary>
    /// Whole seconds, rounded up, at least one: a timeout of zero or less is the one-second minimum a sub-second one
    /// rounds up to. A remaining time computed against a deadline goes negative on the replay after it, where the
    /// expiration the first pass stored decides; a wait armed with a length still suspends once before it expires.
    /// </summary>
    private static int ToWaitTimeoutSeconds(TimeSpan timeout) =>
        timeout <= TimeSpan.Zero ? 1 : DurationSyntax.ToWholeSeconds(timeout, nameof(timeout));

    /// <summary>
    /// The raised signal's stored payload: format id + bytes. <c>ValueFormatId == 0</c> means a
    /// presence-only signal with no payload (<see cref="Value"/> is <c>null</c>). <c>TimedOut</c> is
    /// set when the wait's stored expiration passed with nothing raised.
    /// </summary>
    private protected readonly record struct SignalCheckpointState(byte ValueFormatId, byte[]? Value, bool TimedOut = false);

    /// <summary>
    /// Subclass sink: reads or arms the named signal. Returns normally with its payload when
    /// it is <c>Set</c>; throws the framework signal-suspend signal to re-arm the Job for a subsequent claim.
    /// </summary>
    private protected abstract Task<SignalCheckpointState> WaitSignalCoreAsync(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink for the bounded waits: <paramref name="timeoutSeconds"/> is the wait length the store resolves
    /// against the DB clock into the wait's absolute expiration, written only when the checkpoint is first armed (never on
    /// re-entry, and null for an unbounded wait), and an expired wait answers a timed-out state. The default forwards to
    /// the unbounded sink, so a subclass that overrides only that one keeps working and simply never expires a wait.
    /// </summary>
    private protected virtual Task<SignalCheckpointState> WaitSignalCoreAsync(string name, int? timeoutSeconds, CancellationToken ct) =>
        WaitSignalCoreAsync(name, ct);

    /// <summary>
    /// Subclass sink for a wait bounded by an instant, a group's stored deadline, rather than a length: the store arms
    /// that instant as the wait's expiration, a wait first armed at or past it times out at once, and a timeout
    /// resumes the handler. The default passes the whole seconds left on the host clock, at least one, to the
    /// length-bounded sink, so a subclass without durable storage keeps working.
    /// </summary>
    private protected virtual Task<SignalCheckpointState> WaitSignalUntilCoreAsync(string name, DateTime dueAtUtc, CancellationToken ct) =>
        WaitSignalCoreAsync(name, (int)Math.Max(1, Math.Ceiling((dueAtUtc - DateTime.UtcNow).TotalSeconds)), ct);

    /// <summary>
    /// Subclass sink: deserializes a raised signal payload via the runtime serializer registry.
    /// </summary>
    private protected abstract T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value);
}
