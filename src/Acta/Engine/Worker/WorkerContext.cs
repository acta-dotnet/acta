using System.Collections.Concurrent;

namespace Acta;

/// <summary>
/// Shared mutable worker state threaded between the initializer (writer) and the claim/dispatch loop and dispatcher
/// (readers): populated by <see cref="WorkerRuntimeInitializer"/> and its <see cref="CatalogRegistrar"/> at startup and
/// consulted on the hot path for the worker identity, the descriptor for a claimed job, and whether a claimed job is a
/// recurring slot fire. Every collection stays empty in enqueue-only mode (no <see cref="WorkerRegistration"/>).
/// </summary>
internal sealed class WorkerContext(WorkerRegistration? workerRegistration)
{
    public WorkerRegistration? WorkerRegistration { get; } = workerRegistration;

    public Dictionary<string, int> NamespaceIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> WorkerIdByNamespace { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, int>> DefinitionIdsByNamespace { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// definitions.id to JobDescriptor for hot-path claim dispatch. Built in InitializeAsync after
    /// UpsertDefinitionsAsync returns the DB-assigned ids; consulted in RunOnceAsync to resolve the
    /// descriptor by ClaimedJob.DefinitionId, keeping <c>claim_batch</c> free of any JOIN to
    /// definitions. Each descriptor carries the definition's effective (override-or-default)
    /// policy. Concurrent because the definition-policy reload tick re-overlays entries while
    /// executor threads read them.
    /// </summary>
    public ConcurrentDictionary<int, JobDescriptor> DescriptorByDefinitionId { get; } = new();

    /// <summary>
    /// definitions.id values this worker bounced for want of a handler, and therefore stops claiming.
    /// Per process and never persisted: a build that carries the handler is a new process, so the set
    /// never shrinks in production and an operator needs no way to clear it. Claims are by namespace,
    /// so during a rolling deploy an old worker keeps claiming a definition only the new build can run
    /// and hands it straight back; learning the definition bounds that to the batch already claimed
    /// (every row of it still bounces, and only the first bounce warns): the next claim skips it.
    /// </summary>
    public ConcurrentDictionary<int, byte> UnsupportedDefinitionIds { get; } = new();

    private readonly object _unsupportedGate = new();

    private volatile int[] _unsupportedDefinitionIdsSnapshot = [];

    /// <summary>
    /// The excluded set as an array to bind into a claim, rebuilt on each change. Readers take the
    /// whole array, so a claim binds a set that was coherent at some instant rather than one
    /// enumerated while a bounce was adding to it.
    /// </summary>
    public int[] UnsupportedDefinitionIdsSnapshot => _unsupportedDefinitionIdsSnapshot;

    /// <summary>
    /// Excludes <paramref name="definitionId"/> from this worker's claims. Returns true only for the
    /// first worker thread to add it, which is what makes the bounce warn once per definition.
    /// </summary>
    public bool ExcludeDefinition(int definitionId)
    {
        if (!UnsupportedDefinitionIds.TryAdd(definitionId, 0))
        {
            return false;
        }
        RebuildUnsupportedSnapshot();
        return true;
    }

    /// <summary>
    /// Drops <paramref name="definitionId"/> from the excluded set so this worker claims it again.
    /// A test seam: production never forgets, because the deployment that carries the handler is a
    /// different process.
    /// </summary>
    public bool ForgetUnsupportedDefinition(int definitionId)
    {
        if (!UnsupportedDefinitionIds.TryRemove(definitionId, out _))
        {
            return false;
        }
        RebuildUnsupportedSnapshot();
        return true;
    }

    /// <summary>
    /// Republishes the snapshot, reading the set and storing it under one lock: two concurrent changes
    /// could otherwise interleave so the one that read the smaller set publishes last, hiding an id.
    /// </summary>
    private void RebuildUnsupportedSnapshot()
    {
        lock (_unsupportedGate)
        {
            _unsupportedDefinitionIdsSnapshot = [.. UnsupportedDefinitionIds.Keys];
        }
    }

    /// <summary>
    /// Slot job ids (one per recurring definition): those the startup schedule upsert returned, and any
    /// slot another runtime registered that an executor recognized on claiming it. Consulted on the
    /// execution hot path to branch a claimed slot fire into the recurring path.
    /// </summary>
    public ConcurrentDictionary<long, byte> RecurringSlotJobIds { get; } = new();

    /// <summary>
    /// The <c>sys.recovery</c> slot job id per namespace this worker registered, captured at the startup
    /// schedule upsert. The recovery monitor checks exactly this row and never recreates it: a slot an
    /// operator removed stays removed.
    /// </summary>
    public ConcurrentDictionary<int, long> RecoverySlotJobIdByNamespace { get; } = new();

    /// <summary>
    /// Jobs this worker is mid-execution on: job_id to the attempt's cancellation source + held
    /// locks. The dispatcher registers an entry around each attempt; the heartbeat cancels the
    /// source when extend_worker_leases reports the job left this worker's lease set (externally
    /// cancelled / stolen) and renews every lock the attempt holds while it stays.
    /// </summary>
    public ConcurrentDictionary<long, RunningAttempt> RunningAttempts { get; } = new();

    /// <summary>
    /// Jobs the Buffered claim loop holds between their claim and their executor, to the execution each claim
    /// carries: registered as the claim returns, removed as an executor takes the row. With
    /// <see cref="RunningAttempts"/> this is every execution the heartbeat renews; a row in neither lapses.
    /// </summary>
    public ConcurrentDictionary<long, int> BufferedClaims { get; } = new();

    /// <summary>
    /// Cancel the attempt this process still runs for a job a claim just returned. The claim means the
    /// attempt's lease lapsed and the job was reclaimed, and the replacement shares its key here, so once
    /// the entry is replaced the heartbeat would never reach the stale attempt.
    /// </summary>
    public void CancelDisplacedAttempt(long jobId)
    {
        if (RunningAttempts.TryGetValue(jobId, out var stale))
        {
            stale.Cancel();
        }
    }

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

    /// <summary>
    /// Resolves and validates the (namespaceId, workerId) pair for the registered worker namespace.
    /// Shared by RunOnceAsync and the production claim loop.
    /// </summary>
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
/// A job this worker is mid-execution on: the attempt source that stops the handler, linked from the host token, the
/// execution-timeout source, and the lease source, plus the locks the attempt holds. The lease source is the attempt's
/// deadline: armed at registration and re-armed by the heartbeat on each confirmed renewal, it cancels the attempt
/// <see cref="LeaseRunway"/> past the lease's request start, so an outage stops the handler with nothing watching. A
/// single-shot attempt has no lease source, so a debugger pause never finds it cancelled on resume.
/// </summary>
internal sealed class RunningAttempt : IDisposable
{
    private readonly CancellationTokenSource _timeout = new();
    private readonly CancellationTokenSource? _lease;
    private readonly CancellationTokenSource _attempt;
    private readonly ConcurrentDictionary<LockToken, byte> _heldLocks = new();

    public RunningAttempt(bool leased, CancellationToken hostCt)
    {
        _lease = leased ? new CancellationTokenSource() : null;
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

    /// <summary>
    /// The execution this attempt runs. The heartbeat matches a renewal on job and execution, so a row the
    /// job was claimed again into never renews an older attempt still running under the same job.
    /// </summary>
    public int ExecutionNumber { get; init; }

    public CancellationToken AttemptToken { get; }

    /// <summary>
    /// Whether the cancellation came from the execution-timeout source (not an external cancel or
    /// lease steal), so the runner records a timeout distinctly and routes it through the retry budget.
    /// </summary>
    public bool TimedOut => _timeout.IsCancellationRequested;

    /// <summary>
    /// Cancels the attempt after <paramref name="delay"/>, replacing any earlier deadline, or at once when no runway is
    /// left; a no-op without a lease source, and after the attempt was disposed under a racing heartbeat.
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
    /// completing attempt's dispose is a no-op rather than a fault.
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
