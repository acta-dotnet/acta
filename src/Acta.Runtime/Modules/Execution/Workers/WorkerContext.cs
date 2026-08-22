using System.Collections.Concurrent;
using System.Diagnostics;
using Acta.Runtime.Hosting;
using Acta.Runtime.Services.Locks;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Shared mutable worker state threaded between the initializer (writer) and the claim/dispatch
/// loop + dispatcher (readers). Populated by <see cref="WorkerRuntimeInitializer"/> at startup;
/// consulted on the hot path to resolve the worker identity, the descriptor for a claimed job, and
/// whether a claimed job is a recurring slot fire.
/// </summary>
/// <remarks>
/// All state collections stay empty in enqueue-only mode (no <see cref="WorkerRegistration"/>).
/// </remarks>
internal sealed class WorkerContext(WorkerRegistration? workerRegistration)
{
    public WorkerRegistration? WorkerRegistration { get; } = workerRegistration;

    // Worker-only state. All these fields stay empty in enqueue-only mode.
    public Dictionary<string, int> NamespaceIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> WorkerIdByNamespace { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, int>> DefinitionIdsByNamespace { get; } = new(StringComparer.Ordinal);

    // definitions.id to JobDescriptor for hot-path claim dispatch. Built in InitializeAsync
    // after UpsertDefinitionsAsync returns the DB-assigned ids; consulted in RunOnceAsync to
    // resolve the descriptor by ClaimedJob.DefinitionId, keeping `claim_batch` free of
    // any JOIN to definitions. Each descriptor carries the definition's effective (override-or-
    // default) policy. Concurrent because the definition-policy reload tick re-overlays entries while
    // executor threads read them.
    public ConcurrentDictionary<int, JobDescriptor> DescriptorByDefinitionId { get; } = new();

    // Slot job ids (one per recurring definition) returned by the startup schedule upsert. Consulted
    // on the execution hot path to branch a claimed slot fire into the recurring path.
    public HashSet<long> RecurringSlotJobIds { get; } = [];

    // Jobs this worker is mid-execution on: job_id -> the attempt's cancellation source + held locks.
    // The dispatcher registers an entry around each attempt; the heartbeat cancels the source when
    // extend_worker_leases reports the job left this worker's lease set (externally cancelled / stolen) and
    // extends every lock the attempt holds through the lock store while it stays.
    public ConcurrentDictionary<long, RunningAttempt> RunningAttempts { get; } = new();

    public IReadOnlyDictionary<string, int> RegisteredNamespaceIds => NamespaceIds;

    public bool TryGetDefinitionId(string namespaceName, string jobName, out int definitionId)
    {
        if (DefinitionIdsByNamespace.TryGetValue(namespaceName, out var byName) && byName.TryGetValue(jobName, out var id))
        {
            definitionId = id;
            return true;
        }
        definitionId = 0;
        return false;
    }

    // Resolves and validates the (namespaceId, workerId) pair for the registered worker namespace.
    // Shared by RunOnceAsync and the production claim loop.
    public (int NamespaceId, int WorkerId) ResolveWorker(string namespaceName)
    {
        if (WorkerRegistration is null)
        {
            throw new InvalidOperationException("Worker mode required. Call j.Run<TManifest>(namespaceName, ...) inside UseActa.");
        }
        if (namespaceName != WorkerRegistration.NamespaceName)
        {
            throw new InvalidOperationException(
                $"Namespace mismatch: this runtime is registered as the worker for '{WorkerRegistration.NamespaceName}', not '{namespaceName}'."
            );
        }
        if (!NamespaceIds.TryGetValue(namespaceName, out var namespaceId))
        {
            throw new InvalidOperationException($"Namespace '{namespaceName}' has no id yet. Call InitializeAsync before claiming.");
        }
        return !WorkerIdByNamespace.TryGetValue(namespaceName, out var workerId)
            ? throw new InvalidOperationException($"Worker id for namespace '{namespaceName}' not assigned. Call InitializeAsync first.")
            : ((int NamespaceId, int WorkerId))(namespaceId, workerId);
    }
}

/// <summary>
/// A job this worker is mid-execution on: the per-attempt linked <see cref="CancellationTokenSource"/>
/// (cancelled to stop the handler) plus a conservative monotonic "good until" deadline for every lease it
/// depends on - its job lease and each lock it holds through <c>RunWithLock</c> or the exclusive-key mutex.
/// Two renewers feed these deadlines (the worker heartbeat the job lease, the lock heartbeat each lock via
/// the swappable <see cref="ILockStore"/>) and the watchdog cancels once <see cref="EarliestLeaseGoodUntil"/>
/// nears the unwind margin. Deadlines are monotonic Stopwatch timestamps so a clock correction cannot make
/// a lapsed lease look live; the job-lease field is read/written through Volatile and the locks live in a
/// ConcurrentDictionary, since a renewer writes while the watchdog and handler read.
/// </summary>
internal sealed class RunningAttempt(CancellationTokenSource cts, CancellationTokenSource? timeoutCts = null)
{
    private readonly CancellationTokenSource _cts = cts;
    private readonly CancellationTokenSource? _timeoutCts = timeoutCts;

    // Held lock -> monotonic Stopwatch timestamp its lease is conservatively good until.
    private readonly ConcurrentDictionary<LockToken, long> _heldLocks = new();

    // Job lease's conservative good-until (monotonic Stopwatch timestamp). Written by the worker heartbeat,
    // read by the watchdog on another thread - accessed only through Volatile.
    private long _jobLeaseGoodUntil;

    /// <summary>
    /// The job lease's conservative good-until as a monotonic <see cref="Stopwatch"/> timestamp. Seeded
    /// when the attempt is registered and advanced by the worker heartbeat to <c>requestStart + TTL</c> on
    /// every confirmed renewal (a lower bound on the store-stamped expiry).
    /// </summary>
    public long JobLeaseGoodUntil
    {
        get => Volatile.Read(ref _jobLeaseGoodUntil);
        set => Volatile.Write(ref _jobLeaseGoodUntil, value);
    }

    /// <summary>
    /// The earliest good-until across the job lease and every held lock: the instant one of this attempt's
    /// leases first lapses, and so what the watchdog measures its unwind margin against.
    /// </summary>
    public long EarliestLeaseGoodUntil()
    {
        var earliest = Volatile.Read(ref _jobLeaseGoodUntil);
        foreach (var goodUntil in _heldLocks.Values)
        {
            if (goodUntil < earliest)
            {
                earliest = goodUntil;
            }
        }
        return earliest;
    }

    /// <summary>
    /// Whether the cancellation came from the execution-timeout source (not an external cancel or
    /// lease steal), so the runner records a timeout distinctly and routes it through the retry budget.
    /// </summary>
    public bool TimedOut => _timeoutCts is { IsCancellationRequested: true };

    /// <summary>
    /// Register a lock this attempt now holds, conservatively good until <paramref name="goodUntil"/>
    /// (a monotonic Stopwatch timestamp). Idempotent; a re-acquire refreshes the deadline.
    /// </summary>
    public void TrackLock(LockToken token, long goodUntil) => _heldLocks[token] = goodUntil;

    /// <summary>
    /// Advance a still-held lock's good-until after a confirmed extend. Returns false when the handler
    /// released and untracked the lock while the extend was in flight, so a stale renewal never re-adds it.
    /// </summary>
    public bool ExtendLock(LockToken token, long goodUntil)
    {
        while (_heldLocks.TryGetValue(token, out var current))
        {
            if (_heldLocks.TryUpdate(token, goodUntil, current))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Stop tracking a lock the handler released, so no renewer extends it and no lost-extend is mistaken
    /// for a steal. Called before the store release so the two never race.
    /// </summary>
    public void UntrackLock(LockToken token) => _heldLocks.TryRemove(token, out _);

    /// <summary>
    /// Whether the attempt still holds <paramref name="token"/>, used to tell a genuine steal (still
    /// tracked) from a normal release (already untracked) when an extend fails.
    /// </summary>
    public bool Holds(LockToken token) => _heldLocks.ContainsKey(token);

    /// <summary>
    /// Snapshot of the locks currently held by this attempt, safe to enumerate while the handler
    /// thread adds or removes locks.
    /// </summary>
    public IReadOnlyCollection<LockToken> HeldLocks => [.. _heldLocks.Keys];

    /// <summary>
    /// Cancel the attempt's token. Swallows <see cref="ObjectDisposedException"/> so a renewer/watchdog
    /// tick racing the completing attempt's dispose is a no-op rather than a fault.
    /// </summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException) { }
    }
}
