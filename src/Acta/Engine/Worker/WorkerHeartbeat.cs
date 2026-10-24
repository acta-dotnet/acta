using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The worker heartbeat loop. Every <see cref="JobsOptions.HeartbeatInterval"/> one call stamps <c>last_seen_at_utc</c>
/// and renews every lock hold and execution lease this process holds, so a leased row nothing holds lapses to recovery;
/// it re-arms a renewed attempt's deadline, cancels one the refresh drops, and re-overlays a changed catalog, and a
/// call that throws moves no deadline. The same call takes the namespace's recovery pass when it is due, moving it one
/// lease on, and this worker runs <see cref="RecoveryPass"/> beside the loop: one worker per pass, a busy worker still
/// sweeps, and one that dies mid-pass holds nothing back.
/// </summary>
internal sealed class WorkerHeartbeat(
    WorkersOperations workers,
    DefinitionsOperations definitions,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    InFlightJobRegistry registry,
    RecoveryPass recoveryPass,
    ILogger log,
    ActaMetrics? metrics = null
)
{
    private readonly WorkersOperations _workers = workers;
    private readonly DefinitionsOperations _definitions = definitions;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly InFlightJobRegistry _registry = registry;
    private readonly TimeSpan _interval = options.Value.HeartbeatInterval;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly TimeSpan _leaseRunway = InFlightJob.LeaseRunway(options.Value);

    // The recovery passes this loop took, chained so one runs at a time; the beat never waits on one.
    private Task _recovery = Task.CompletedTask;

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
    /// Serializes TickAsync: the heartbeat loop and StampDrainingAsync's immediate stamp can fire
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
                await TickAsync(takeRecovery: true, ct);
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
                    await TickAsync(takeRecovery: true, ct);
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
        finally
        {
            await _recovery.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

        await TickAsync(takeRecovery: false, ct);
    }

    /// <summary>
    /// One worker-heartbeat pass: renew the leases of the executions this process holds, re-arm each still-owned
    /// entry's lease deadline and cancel any the refresh authoritatively dropped, then re-overlay the catalog
    /// when it changed. The deterministic single-shot tests drive via WorkerRuntime.RunHeartbeatOnceAsync; it takes no
    /// recovery pass, which only the loop does.
    /// </summary>
    public Task TickAsync(CancellationToken ct) => TickAsync(takeRecovery: false, ct);

    private async Task TickAsync(bool takeRecovery, CancellationToken ct)
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
                // A draining worker takes no new pass: it is about to stop, and the pass would hold up its exit.
                int? recoveryInterval = takeRecovery && _context.RunsRecovery && !_draining ? _leaseTtlSeconds : null;
                foreach (var (namespaceName, workerId) in _context.WorkerIdByNamespace)
                {
                    var answer = await _workers.ExtendWorkerLeasesAsync(
                        new ExtendWorkerLeasesCommand(workerId, _leaseTtlSeconds, _draining, held, heldLocks, recoveryInterval),
                        ct
                    );
                    if (answer.RecoveryClaimed)
                    {
                        RunRecovery(namespaceName, ct);
                    }
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
                    await RenewSkippedAsync(workerId, answer.Leases, renewed, ct);
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

    /// <summary>How many times one beat asks again for held rows another transaction had locked.</summary>
    internal const int SkippedRenewalRetries = 8;

    /// <summary>The pause before each ask again: longer than a fenced handler write holds its job's row.</summary>
    internal static readonly TimeSpan SkippedRenewalPause = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Asks again, a few times within this beat, for the held rows the renewal skipped. Every handler write holds its
    /// job's row until it commits (docs/sql-internals.md, "Execution fence"), so a handler that writes in a tight loop
    /// would otherwise lose beat after beat until its lease deadline stopped it. Each ask skips locked rows like the
    /// first and never waits on one; a fault is the tick's, which then moves nothing.
    /// </summary>
    private async Task RenewSkippedAsync(
        int workerId,
        IReadOnlyList<ExtendWorkerLeasesLease> leases,
        HashSet<(long JobId, int ExecutionNumber)> renewed,
        CancellationToken ct
    )
    {
        List<(long JobId, int ExecutionNumber)> skipped = [.. leases.Where(l => !l.Renewed).Select(l => (l.JobId, l.ExecutionNumber))];
        for (var retry = 0; retry < SkippedRenewalRetries && skipped.Count > 0; retry++)
        {
            await Task.Delay(SkippedRenewalPause, ct);
            var again = await _workers.ExtendWorkerLeasesAsync(
                new ExtendWorkerLeasesCommand(workerId, _leaseTtlSeconds, _draining, skipped, []),
                ct
            );
            skipped.Clear();
            foreach (var lease in again.Leases)
            {
                if (lease.Renewed)
                {
                    renewed.Add((lease.JobId, lease.ExecutionNumber));
                }
                else
                {
                    skipped.Add((lease.JobId, lease.ExecutionNumber));
                }
            }
        }
    }

    /// <summary>
    /// Runs the pass this loop's call took, after any earlier one, on a task of its own. A pass that fails waits out
    /// the interval its call already moved, like any worker's; a hard stop cancels it, and the next pass falls to
    /// whichever worker takes it when it comes due.
    /// </summary>
    private void RunRecovery(string namespaceName, CancellationToken ct)
    {
        if (!_context.NamespaceIds.TryGetValue(namespaceName, out var namespaceId))
        {
            return;
        }

        var previous = _recovery;
        _recovery = Task.Run(
            async () =>
            {
                // An earlier pass's fault is its own: this pass runs regardless.
                await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                var outcome = "completed";
                try
                {
                    await recoveryPass.RunAsync(namespaceId, namespaceName, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // A hard stop; the next pass falls to whichever worker takes it.
                    return;
                }
                catch (Exception ex)
                {
                    outcome = "failed";
                    _log.LogError(
                        ex,
                        "Namespace ({Namespace}): the recovery pass failed; the next one runs when it comes due.",
                        namespaceName
                    );
                }

                try
                {
                    metrics?.RecordRecoveryPass(namespaceName, outcome);
                }
                catch (Exception)
                {
                    // A listener that throws must not fault the chain.
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>
    /// Re-reads the namespace's catalog: re-overlays the effective policy onto the live descriptors, so an operator
    /// override edited in the dashboard reaches the hot path (backoff, timeout, deadline, retention, max attempts), and
    /// records the retired definitions, so a retire stops their jobs, both within one heartbeat and no restart required.
    /// Runs only when the newest definition change moved: two edits of one namespace committed within the same instant
    /// in reverse order can leave one of them unseen until the next edit.
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
            if (_context.DescriptorByDefinitionId.TryGetValue(c.DefinitionId, out var descriptor))
            {
                _context.DescriptorByDefinitionId[c.DefinitionId] = EffectivePolicyOverlay.Apply(descriptor, c.Effective);
            }
        }
        _context.ReplaceRetiredDefinitions(
            catalog
                .Where(c => c.Status == DefinitionStatusCode.Retired)
                .ToDictionary(c => c.DefinitionId, c => c.Effective.JobRetentionSeconds)
        );
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
        var ns = _workerRegistration?.NamespaceName;
        foreach (var (entry, locks) in snapshot)
        {
            if (!live.Contains(entry.Key) && entry.State != InFlightJobState.Finalizing)
            {
                entry.Cancel();
                continue;
            }
            if (renewed.Contains(entry.Key))
            {
                var headroom = entry.LeaseHeadroom;
                entry.ArmLease(runway);
                try
                {
                    if (ns is not null && headroom is { } value)
                    {
                        metrics?.RecordLeaseHeadroom(ns, value);
                    }
                }
                catch (Exception)
                {
                    // A listener that throws must not stop the feed: the renewals it reports have landed.
                }
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
