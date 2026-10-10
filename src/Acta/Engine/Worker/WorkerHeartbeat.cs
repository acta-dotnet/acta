using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The runtime-owned worker heartbeat loop. Every <see cref="JobsOptions.HeartbeatInterval"/> one
/// <see cref="IWorkersOperations.ExtendWorkerLeasesAsync"/> call stamps <c>workers.last_seen_at_utc</c> and renews every lock
/// hold and execution lease this process holds, so a leased row nothing holds lapses to recovery. It re-arms a running
/// attempt's lease deadline on a confirmed renewal, cancels one whose execution or held lock an authoritative refresh
/// drops, and re-overlays the definitions' effective policy when the namespace's catalog changed. A call that throws
/// moves no deadline, so an outage cancels each attempt by its deadline alone; a no-op in enqueue-only mode.
/// </summary>
internal sealed class WorkerHeartbeat(
    IWorkersOperations workers,
    IDefinitionsOperations definitions,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    InFlightJobRegistry registry,
    ILogger log
)
{
    private readonly IWorkersOperations _workers = workers;
    private readonly IDefinitionsOperations _definitions = definitions;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly InFlightJobRegistry _registry = registry;
    private readonly TimeSpan _interval = options.Value.HeartbeatInterval;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly TimeSpan _leaseRunway = InFlightJob.LeaseRunway(options.Value);

    // The namespace's newest definitions.modified_at_utc at the last overlay. Touched only under the tick gate.
    private DateTime? _definitionsModifiedAtUtc;
    private readonly ILogger _log = log;

    /// <summary>
    /// Set once when the runtime begins a graceful drain. Every subsequent lease refresh then flips
    /// the worker Active to Draining (idempotent once Draining), so the draining phase is visible
    /// without a dedicated routine. Volatile: written from the host's StopAsync thread, read on the
    /// heartbeat loop.
    /// </summary>
    private volatile bool _draining;

    /// <summary>
    /// Serializes TickAsync: the heartbeat loop and BeginDrainAsync's immediate stamp can fire
    /// concurrently, and a tick must not race itself (double lease extends, double feed).
    /// </summary>
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        _log.LogInformation("WorkerRuntime: starting heartbeat loop (interval {DurationMs}ms).", (long)_interval.TotalMilliseconds);

        try
        {
            // Immediate first tick: stamps last_seen at startup instead of waiting out the first
            // interval (PeriodicTimer fires after one period).
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown landed before the first tick completed; the periodic wait below observes
                // the same cancellation and exits cleanly through the outer catch.
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "WorkerRuntime: initial heartbeat tick failed; retrying on the interval.");
            }

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "WorkerRuntime: heartbeat tick failed; retrying next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// Enters in-memory draining mode without I/O: every subsequent lease refresh flips the worker Active
    /// to Draining. <see cref="StampDrainingAsync"/> performs the immediate persistence pass separately so
    /// the host can bound and parallelize it. In-flight leases keep being extended through the drain.
    /// </summary>
    public void BeginDrain() => _draining = true;

    /// <summary>Immediately persists the already-entered draining state.</summary>
    public async Task StampDrainingAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        await TickAsync(ct);
    }

    /// <summary>
    /// One worker-heartbeat pass: renew the leases of the executions this process holds, re-arm each still-owned
    /// entry's lease deadline and cancel any the refresh authoritatively dropped, then re-overlay the catalog
    /// when it changed. The deterministic single-shot the loop drives per tick; tests drive it via
    /// WorkerRuntime.RunHeartbeatOnceAsync.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        // Refresh every worker's leases first and union the still-owned ids, THEN feed once: the registry
        // holds every worker's jobs, so feeding per worker would see another worker's jobs as "dropped" and
        // wrongly cancel them.
        await _tickGate.WaitAsync(ct);
        try
        {
            // Snapshot the registry BEFORE the extend queries run, and judge only that snapshot. An entry
            // present here was registered after its claim committed, so an extend query that starts
            // afterwards sees its row; an entry registered mid-tick is judged on the next tick, where
            // judging it now would find it unanswered and wrongly cancel it. Judging an entry removed
            // meanwhile is harmless: the registry disposes it after removing it, so a late Cancel() or
            // ArmLease() lands on a token nothing reads anymore or a disposed source, which both swallow.
            var snapshot = _registry.Snapshot().Select(entry => (Entry: entry, Locks: entry.HeldLocks)).ToArray();
            List<(long JobId, int ExecutionNumber)> held = [.. snapshot.Select(pair => pair.Entry.Key)];
            List<LockToken> heldLocks = [.. snapshot.SelectMany(pair => pair.Locks)];

            // Renew the held leases and locks and union the still-owned ids. Capture the request-start
            // BEFORE the call: the store stamps the renewed expiry no earlier than this instant, so
            // requestStart + TTL is a conservative lower bound on when the lease actually lapses (unlike
            // a post-response `now + TTL`, which overestimates the lease by the round-trip latency). A
            // transient store fault throws rather than returning: tolerate it (leave `live` null) and
            // move nothing this tick, so the deadlines cancel if the outage outlasts them. The renewal
            // is not deadline-critical, so it is not bounded here beyond the caller's ct.
            // A row another transaction held is still owned but was not renewed, so it stays live without
            // moving its deadline.
            var renewRequestedAt = Stopwatch.GetTimestamp();
            HashSet<(long JobId, int ExecutionNumber)>? live = [];
            HashSet<(long JobId, int ExecutionNumber)> renewed = [];
            HashSet<string> renewedLocks = new(StringComparer.Ordinal);
            DateTime? definitionsModifiedAtUtc = null;
            try
            {
                foreach (var workerId in _context.WorkerIdByNamespace.Values)
                {
                    var answer = await _workers.ExtendWorkerLeasesAsync(workerId, _leaseTtlSeconds, _draining, held, heldLocks, ct);
                    renewedLocks.UnionWith(answer.RenewedLockKeys);
                    definitionsModifiedAtUtc = answer.DefinitionsModifiedAtUtc;
                    foreach (var lease in answer.Leases)
                    {
                        live.Add((lease.JobId, lease.ExecutionNumber));
                        if (lease.Renewed)
                        {
                            renewed.Add((lease.JobId, lease.ExecutionNumber));
                        }
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Transient store fault during normal operation: leave `live` non-authoritative and move
                // nothing. A fault at shutdown (ct cancelled) is not caught here - it flows to the loop's
                // existing cancellation handling.
                _log.LogWarning(ex, "WorkerRuntime: could not extend worker leases this tick; the lease deadlines stand.");
                live = null;
            }

            FeedLeases(snapshot, live, renewed, renewedLocks, renewRequestedAt);
            if (live is not null && definitionsModifiedAtUtc != _definitionsModifiedAtUtc)
            {
                await OverlayDefinitionsAsync(ct);
                _definitionsModifiedAtUtc = definitionsModifiedAtUtc;
            }
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>
    /// Re-reads the namespace's effective policy and re-overlays it onto the live descriptors, so an operator override
    /// edited in the dashboard reaches the hot path (backoff, timeout, deadline, retention, max attempts) within one
    /// heartbeat, no restart required. Runs only when the newest definition change moved: two edits of one namespace
    /// committed within the same instant in reverse order can leave one of them unseen until the next edit.
    /// </summary>
    private async Task OverlayDefinitionsAsync(CancellationToken ct)
    {
        if (_workerRegistration is not { NamespaceName: var ns } || !_context.NamespaceIds.TryGetValue(ns, out var namespaceId))
        {
            return;
        }

        var catalog = (await _definitions.GetDefinitionContractsAsync(namespaceId, ct)).Contracts;
        foreach (var c in catalog)
        {
            if (_context.DescriptorByDefinitionId.TryGetValue(c.Id, out var descriptor))
            {
                _context.DescriptorByDefinitionId[c.Id] = EffectivePolicyOverlay.Apply(descriptor, c.Effective);
            }
        }
        MeterConsistencyCheck.Check(ns, catalog, _log);
    }

    /// <summary>
    /// For each entry in the pre-extend snapshot, matched on job and execution: a renewal re-arms its lease deadline
    /// from the request start; a row skipped because another transaction held it keeps its deadline; an execution the
    /// refresh dropped (operator cancel, a stolen or reclaimed lease, or the job claimed again) is cancelled now unless
    /// it is Finalizing, where the completion write's compare-and-swap decides; and an entry still holding a lock the
    /// call did not renew is cancelled. <c>live</c> is null when the refresh threw: a definitive loss cannot be told
    /// from a blip, so nothing moves and the deadlines decide.
    /// </summary>
    private void FeedLeases(
        (InFlightJob Entry, IReadOnlyCollection<LockToken> Locks)[] snapshot,
        HashSet<(long JobId, int ExecutionNumber)>? live,
        HashSet<(long JobId, int ExecutionNumber)> renewed,
        HashSet<string> renewedLocks,
        long renewRequestedAt
    )
    {
        if (live is null)
        {
            return;
        }

        var runway = _leaseRunway - Stopwatch.GetElapsedTime(renewRequestedAt);
        foreach (var (entry, locks) in snapshot)
        {
            if (!live.Contains(entry.Key) && entry.State != InFlightJobState.Finalizing)
            {
                entry.Cancel();
                continue;
            }
            if (renewed.Contains(entry.Key))
            {
                entry.ArmLease(runway);
            }

            // A lock the handler released while the renewal was in flight is no longer held, so only a lock it
            // still relies on, now taken by another holder, ends its critical section.
            if (locks.Any(token => !renewedLocks.Contains(token.Key) && entry.Holds(token)))
            {
                entry.Cancel();
                _log.LogWarning("WorkerRuntime: cancelling job {JobId}; a held lock was lost.", entry.Claim.JobId);
            }
        }
    }
}
