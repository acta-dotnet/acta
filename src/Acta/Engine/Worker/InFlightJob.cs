using System.Collections.Concurrent;

namespace Acta;

/// <summary>
/// One claim this worker holds, from the claim to its completion write: the attempt source that stops the handler,
/// linked from the host token, the execution-timeout source, and the lease source, plus the locks the attempt holds.
/// The lease source is the entry's deadline: armed at the claim and re-armed by the heartbeat on each confirmed
/// renewal, it cancels the attempt <see cref="LeaseRunway"/> past the lease's request start, so an outage stops the
/// handler with nothing watching. A single-shot entry, built without a runway, has no lease source, so a debugger
/// pause never finds it cancelled on resume.
/// </summary>
internal sealed class InFlightJob : IDisposable
{
    private readonly CancellationTokenSource _timeout = new();
    private readonly CancellationTokenSource? _lease;
    private readonly CancellationTokenSource _attempt;
    private readonly ConcurrentDictionary<LockToken, byte> _heldLocks = new();
    private volatile InFlightJobState _state;
    private int _permit;

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

    public RecurringFirePlan? Fire { get; set; }

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
    /// Cancels the attempt after <paramref name="delay"/>, replacing any earlier deadline, or at once when no runway is
    /// left; a no-op without a lease source, and after the entry was disposed under a racing heartbeat.
    /// </summary>
    public void ArmLease(TimeSpan delay)
    {
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

    public void ArmTimeout(TimeSpan delay) => _timeout.CancelAfter(delay);

    /// <summary>True exactly once for an entry built holding one of the registry's permits, so every exit may call it.</summary>
    public bool ReleasePermit() => Interlocked.Exchange(ref _permit, 0) == 1;

    public void TrackLock(LockToken token) => _heldLocks[token] = 0;

    /// <summary>
    /// Stop tracking a lock the handler released, so the heartbeat no longer renews it and a release is never
    /// mistaken for a steal. Called before the store release so the two never race.
    /// </summary>
    public void UntrackLock(LockToken token) => _heldLocks.TryRemove(token, out _);

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
