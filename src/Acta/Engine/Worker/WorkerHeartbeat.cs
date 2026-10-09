using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The runtime-owned worker heartbeat loop. Every <see cref="JobsOptions.HeartbeatInterval"/> one
/// <see cref="WorkerLifecycle.ExtendWorkerLeasesAsync"/> call stamps <c>workers.last_seen_at_utc</c> and renews the lease of
/// every execution this process holds, so a leased row nothing holds lapses to recovery. It feeds a running attempt's
/// job-lease deadline on a confirmed renewal, cancels one whose execution an authoritative refresh drops, and re-overlays
/// the definitions' effective policy when the namespace's catalog changed. Deadlines on an outage are
/// <see cref="AttemptWatchdog"/>'s and handler locks <see cref="LockLeaseHeartbeat"/>'s; a no-op in enqueue-only mode.
/// </summary>
internal sealed class WorkerHeartbeat(
    WorkerLifecycle workers,
    GetDefinitionContractsHandler getDefinitionContracts,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger log
)
{
    private readonly WorkerLifecycle _workers = workers;
    private readonly GetDefinitionContractsHandler _getDefinitionContracts = getDefinitionContracts;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly TimeSpan _interval = options.Value.HeartbeatInterval;
    private readonly TimeSpan _safetyPoll = options.Value.SafetyPollInterval;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;

    // The namespace's newest definitions.modified_at_utc at the last overlay, and when that overlay ran. A row
    // committed after an overlay's read but stamped at or before the newest value that read saw never moves the
    // stamp, so the overlay also repeats once SafetyPollInterval has passed. Touched only under the tick gate.
    private DateTime? _definitionsModifiedAtUtc;
    private long? _overlaidAt;

    /// <summary>
    /// Lease TTL as monotonic Stopwatch ticks. The job-lease deadline is measured on Stopwatch, not
    /// wall time: it cannot jump backward on an NTP/VM correction (which would make a lapsed lease
    /// look live), and it is readable even while the store is down.
    /// </summary>
    private readonly long _ttlStopwatchTicks = (long)(options.Value.LeaseTtlSeconds * (double)Stopwatch.Frequency);
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
    /// One worker-heartbeat pass: renew the leases of the executions this process holds, feed each still-owned
    /// attempt's job-lease deadline and cancel any the refresh authoritatively dropped, then re-overlay the catalog
    /// when it changed. The deterministic single-shot the loop drives per tick; tests drive it via
    /// WorkerRuntime.RunHeartbeatOnceAsync.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        // Refresh every worker's leases first and union the still-owned ids, THEN feed once. A process can
        // host several workers but RunningAttempts is shared across them, so feeding per worker would see
        // another worker's jobs as "dropped" and wrongly cancel them.
        await _tickGate.WaitAsync(ct);
        try
        {
            // Snapshot RunningAttempts BEFORE the extend queries run, and reconcile only that snapshot.
            // An attempt present here was dispatched after its claim committed, so a read-committed
            // extend query that starts afterwards is guaranteed to see it as live; an attempt that gets
            // registered mid-tick (after this snapshot) is simply reconciled on the next tick instead of
            // this one. Without the snapshot, a job claimed+dispatched into RunningAttempts while the
            // extend query for another worker was still in flight would be invisible to this tick's
            // "live" set (it was claimed too late to be extended) yet present when reconciliation
            // enumerated the live dictionary afterwards, and would be wrongly cancelled. Cancel() on an
            // attempt that has already completed by the time it's reconciled is harmless: JobExecutor
            // removes the attempt from RunningAttempts before disposing its CancellationTokenSource, so a
            // late Cancel() either lands on a token nothing reads anymore or hits the already-disposed
            // source, which RunningAttempt.Cancel() swallows.
            var snapshot = _context.RunningAttempts.ToArray();
            List<(long JobId, int ExecutionNumber)> held =
            [
                .. snapshot.Select(attempt => (attempt.Key, attempt.Value.ExecutionNumber)),
                .. _context.BufferedClaims.Select(claim => (claim.Key, claim.Value)),
            ];

            // Renew this worker's job leases and union the still-owned ids. Capture the request-start
            // BEFORE the call: the store stamps the renewed expiry no earlier than this instant, so
            // requestStart + TTL is a conservative lower bound on when the lease actually lapses (unlike
            // a post-response `now + TTL`, which overestimates the lease by the round-trip latency). A
            // transient store fault throws rather than returning: tolerate it (leave `live` null), feed
            // nothing this tick, and let the watchdog cancel if the outage outlasts the deadline. The
            // renewal is not deadline-critical (the watchdog enforces on its own loop), so it is not
            // bounded here beyond the caller's ct.
            // A row another transaction held is still owned but was not renewed, so it stays live without
            // moving its deadline.
            var renewRequestedAt = Stopwatch.GetTimestamp();
            HashSet<(long JobId, int ExecutionNumber)>? live = [];
            HashSet<(long JobId, int ExecutionNumber)> renewed = [];
            DateTime? definitionsModifiedAtUtc = null;
            try
            {
                foreach (var workerId in _context.WorkerIdByNamespace.Values)
                {
                    var answer = await _workers.ExtendWorkerLeasesAsync(workerId, _leaseTtlSeconds, _draining, held, ct);
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
                // Transient store fault during normal operation: leave `live` non-authoritative and feed
                // nothing. A fault at shutdown (ct cancelled) is not caught here - it flows to the loop's
                // existing cancellation handling.
                _log.LogWarning(
                    ex,
                    "WorkerRuntime: could not extend worker leases this tick; leaving job-lease deadlines for the watchdog."
                );
                live = null;
            }

            FeedJobLeases(snapshot, live, renewed, renewRequestedAt);
            if (
                live is not null
                && (
                    _overlaidAt is not { } overlaidAt
                    || definitionsModifiedAtUtc != _definitionsModifiedAtUtc
                    || Stopwatch.GetElapsedTime(overlaidAt) > _safetyPoll
                )
            )
            {
                await OverlayDefinitionsAsync(ct);
                _definitionsModifiedAtUtc = definitionsModifiedAtUtc;
                _overlaidAt = Stopwatch.GetTimestamp();
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
    /// heartbeat, no restart required.
    /// </summary>
    private async Task OverlayDefinitionsAsync(CancellationToken ct)
    {
        if (_workerRegistration is not { NamespaceName: var ns } || !_context.NamespaceIds.TryGetValue(ns, out var namespaceId))
        {
            return;
        }

        var catalog = (await _getDefinitionContracts.HandleAsync(new GetDefinitionContractsQuery(namespaceId), ct)).Contracts;
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
    /// For each attempt this process was running as of the pre-extend snapshot, matched on job and execution: a
    /// renewal by an authoritative refresh feeds its job-lease deadline forward; a row the refresh reported but skipped
    /// because another transaction held it keeps its deadline for the next beat; an execution the authoritative refresh
    /// dropped (operator cancel, a stolen or reclaimed lease, or the job claimed again into a newer execution) is
    /// cancelled now. <c>live</c> is null when the refresh threw (store unreachable): a definitive gone cannot be told
    /// from a blip, so nothing is fed or cancelled and the watchdog cancels only once the deadline is about to lapse.
    /// </summary>
    private void FeedJobLeases(
        KeyValuePair<long, RunningAttempt>[] snapshot,
        HashSet<(long JobId, int ExecutionNumber)>? live,
        HashSet<(long JobId, int ExecutionNumber)> renewed,
        long renewRequestedAt
    )
    {
        if (live is null)
        {
            return;
        }

        var goodUntil = renewRequestedAt + _ttlStopwatchTicks;
        foreach (var (jobId, attempt) in snapshot)
        {
            var execution = (jobId, attempt.ExecutionNumber);
            if (renewed.Contains(execution))
            {
                // Confirmed renewal: the request-start is a lower bound on the store-stamped expiry.
                attempt.JobLeaseGoodUntil = goodUntil;
            }
            else if (!live.Contains(execution))
            {
                // Definitive loss under an authoritative refresh. Stop it now rather than waiting out the
                // watchdog.
                attempt.Cancel();
            }
        }
    }
}
