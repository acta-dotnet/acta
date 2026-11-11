using System.Collections.Concurrent;
using System.Diagnostics;

namespace Acta;

/// <summary>
/// One claim this worker holds, from the claim to its completion write: the attempt source that stops the handler,
/// linked from the host token, the execution-timeout source, and the lease source, plus the locks the attempt holds.
/// The lease source is the entry's deadline: the earliest of the execution lease's and each held lock's, each one
/// <see cref="LeaseRunway"/> past the request start that last confirmed it (the claim, an acquire, a renewal), so an
/// outage stops the handler with nothing watching. A single-shot entry, built without a runway, has no lease source,
/// so a debugger pause never finds it cancelled on resume.
/// </summary>
internal sealed class InFlightJob : IDisposable
{
    private readonly CancellationTokenSource _timeout = new();
    private readonly CancellationTokenSource? _lease;
    private readonly CancellationTokenSource _attempt;

    // Each held lock's own deadline, as a Stopwatch timestamp.
    private readonly ConcurrentDictionary<LockToken, long> _heldLocks = new();

    // Orders the deadline moves, so a renewal and an acquire racing each other never arm a stale deadline last.
    private readonly Lock _deadlineGate = new();
    private volatile InFlightJobState _state;
    private int _permit;

    // The execution lease's own deadline, and the earliest of it and the locks', when the lease source fires; both
    // Stopwatch timestamps.
    private long _executionDeadline;
    private long _leaseDeadline;

    public InFlightJob(ClaimedJob claim, TimeSpan? leaseRunway, CancellationToken hostCt, bool holdsPermit = false)
    {
        Claim = claim;
        _permit = holdsPermit ? 1 : 0;
        if (leaseRunway is { } runway)
        {
            _lease = new CancellationTokenSource();
            ArmLease(runway);
        }
        _attempt = CancellationTokenSource.CreateLinkedTokenSource(hostCt, _timeout.Token, _lease?.Token ?? CancellationToken.None);
        AttemptToken = _attempt.Token;
    }

    /// <summary>
    /// How long past a lease's request start an attempt runs: the TTL less an unwind margin of half the base runway
    /// (TTL - HeartbeatInterval), which is the midpoint of the two. The margin stays below the runway a healthy attempt
    /// has left before each renewal for every config the validator permits (TTL at least twice the interval), and the
    /// other half is the handler's window to observe cancellation and unwind before the lease lapses.
    /// </summary>
    public static TimeSpan LeaseRunway(JobsOptions options) =>
        (TimeSpan.FromSeconds(options.LeaseTtlSeconds) + options.HeartbeatInterval) / 2;

    public ClaimedJob Claim { get; }

    public (long JobId, int ExecutionNumber) Key => (Claim.JobId, Claim.ExecutionNumber);

    /// <summary>The descriptor the executor ran it under; null for a claim handed back unrun.</summary>
    public JobDescriptor? Descriptor { get; set; }

    public RuntimeJobContext? Context { get; set; }

    public ExecutionCompletion? Completion { get; set; }

    public InFlightJobState State
    {
        get => _state;
        set => _state = value;
    }

    public CancellationToken AttemptToken { get; }

    /// <summary>
    /// Whether the cancellation came from the execution-timeout source (not an external cancel or
    /// lease steal), so the runner records a timeout distinctly and routes it through the retry budget.
    /// </summary>
    public bool TimedOut => _timeout.IsCancellationRequested;

    /// <summary>
    /// Moves the execution lease's deadline <paramref name="delay"/> out, replacing the earlier one, and re-arms the
    /// lease source at the earliest deadline the entry holds, which cancels at once when no runway is left; a no-op
    /// without a lease source, and after the entry was disposed under a racing heartbeat.
    /// </summary>
    public void ArmLease(TimeSpan delay)
    {
        lock (_deadlineGate)
        {
            _executionDeadline = DeadlineAfter(delay);
            ArmEarliest();
        }
    }

    /// <summary>
    /// Moves the deadline of each still-held lock in <paramref name="renewed"/> <paramref name="delay"/> out, then re-arms
    /// the lease source at the earliest deadline the entry holds.
    /// </summary>
    public void ConfirmLocks(IEnumerable<LockToken> renewed, TimeSpan delay)
    {
        lock (_deadlineGate)
        {
            var deadline = DeadlineAfter(delay);
            foreach (var token in renewed)
            {
                if (_heldLocks.ContainsKey(token))
                {
                    _heldLocks[token] = deadline;
                }
            }
            ArmEarliest();
        }
    }

    private static long DeadlineAfter(TimeSpan delay) => Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);

    // Call under _deadlineGate.
    private void ArmEarliest()
    {
        var deadline = _executionDeadline;
        foreach (var (_, lockDeadline) in _heldLocks)
        {
            deadline = Math.Min(deadline, lockDeadline);
        }

        Volatile.Write(ref _leaseDeadline, deadline);
        var delay = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
        try
        {
            if (delay <= TimeSpan.Zero)
            {
                _lease?.Cancel();
            }
            else
            {
                _lease?.CancelAfter(delay);
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>How long the attempt has before its lease source fires, negative once it has; null without one.</summary>
    public TimeSpan? LeaseHeadroom =>
        _lease is null ? null : Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), Volatile.Read(ref _leaseDeadline));

    public void ArmTimeout(TimeSpan delay) => _timeout.CancelAfter(delay);

    /// <summary>True exactly once for an entry built holding one of the registry's permits, so every exit may call it.</summary>
    public bool ReleasePermit() => Interlocked.Exchange(ref _permit, 0) == 1;

    /// <summary>
    /// Tracks a lock the attempt acquired, so the heartbeat renews it, with <paramref name="runway"/> left before its
    /// lease runs out: the lease runway less the time since the acquire's request started, or null for an attempt
    /// without a lease source. An acquire whose answer came late may hand over a hold already near its expiry, and the
    /// attempt must stop before another holder can take it.
    /// </summary>
    public void TrackLock(LockToken token, TimeSpan? runway)
    {
        lock (_deadlineGate)
        {
            _heldLocks[token] = runway is { } left ? DeadlineAfter(left) : long.MaxValue;
            ArmEarliest();
        }
    }

    /// <summary>
    /// Stop tracking a lock the handler released, so the heartbeat no longer renews it and a release is never
    /// mistaken for a steal, and drop its deadline. Called before the store release so the two never race.
    /// </summary>
    public void UntrackLock(LockToken token)
    {
        lock (_deadlineGate)
        {
            if (_heldLocks.TryRemove(token, out _))
            {
                ArmEarliest();
            }
        }
    }

    /// <summary>
    /// Whether the attempt still holds <paramref name="token"/>, used to tell a genuine steal (still
    /// tracked) from a normal release (already untracked) when a renewal misses it.
    /// </summary>
    public bool Holds(LockToken token) => _heldLocks.ContainsKey(token);

    /// <summary>
    /// Snapshot of the locks currently held by this attempt, safe to enumerate while the handler
    /// thread adds or removes locks.
    /// </summary>
    public IReadOnlyCollection<LockToken> HeldLocks => [.. _heldLocks.Keys];

    /// <summary>
    /// Cancel the attempt's token. Swallows <see cref="ObjectDisposedException"/> so a heartbeat tick racing the
    /// completing entry's dispose is a no-op rather than a fault.
    /// </summary>
    public void Cancel()
    {
        try
        {
            _attempt.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _attempt.Dispose();
        _timeout.Dispose();
        _lease?.Dispose();
    }
}
