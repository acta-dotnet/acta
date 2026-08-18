using System.Diagnostics;
using Acta.Runtime.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// The runtime-owned worker heartbeat loop. Every <see cref="JobsOptions.HeartbeatInterval"/> it renews
/// this worker's batched job leases + <c>workers.last_seen_at_utc</c> via
/// <see cref="IWorkerStore.ExtendWorkerLeasesAsync"/> and feeds each running attempt's job-lease deadline
/// on a confirmed renewal, cancelling one only when an authoritative refresh drops its job (operator cancel
/// or a reclaimed lease). It does not enforce deadlines on an outage - the <see cref="AttemptWatchdog"/>
/// does - nor renew handler locks - <see cref="LockLeaseHeartbeat"/> does, so the swappable
/// <see cref="Acta.Runtime.Services.Locks.ILockStore"/> (relational today, Redis tomorrow) stays a distinct failure
/// domain. Runs on its own <see cref="PeriodicTimer"/>; a no-op in enqueue-only mode.
/// </summary>
internal sealed class WorkerHeartbeat(
    IWorkerStore workers,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger log
)
{
    private readonly IWorkerStore _workers = workers;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly TimeSpan _interval = options.Value.HeartbeatInterval;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;

    // Lease TTL as monotonic Stopwatch ticks. The job-lease deadline is measured on Stopwatch, not wall
    // time: it cannot jump backward on an NTP/VM correction (which would make a lapsed lease look live),
    // and it is readable even while the store is down.
    private readonly long _ttlStopwatchTicks = (long)(options.Value.LeaseTtlSeconds * (double)Stopwatch.Frequency);
    private readonly ILogger _log = log;

    // Set once when the runtime begins a graceful drain. Every subsequent lease refresh then flips the
    // worker Active -> Draining (idempotent once Draining), so the draining phase is visible without a
    // dedicated routine. Volatile: written from the host's StopAsync thread, read on the heartbeat loop.
    private volatile bool _draining;

    // Serializes TickAsync: the heartbeat loop and BeginDrainAsync's immediate stamp can fire concurrently,
    // and a tick must not race itself (double lease extends, double feed).
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

    // One worker-heartbeat pass: renew this worker's batched job leases, then feed each still-owned
    // attempt's job-lease deadline and cancel any the refresh authoritatively dropped. The deterministic
    // single-shot the loop drives per tick; tests drive it via WorkerRuntime.RunHeartbeatOnceAsync.
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

            // Renew this worker's job leases and union the still-owned ids. Capture the request-start
            // BEFORE the call: the store stamps the renewed expiry no earlier than this instant, so
            // requestStart + TTL is a conservative lower bound on when the lease actually lapses (unlike
            // a post-response `now + TTL`, which overestimates the lease by the round-trip latency). A
            // transient store fault throws rather than returning: tolerate it (leave `live` null), feed
            // nothing this tick, and let the watchdog cancel if the outage outlasts the deadline. The
            // renewal is not deadline-critical (the watchdog enforces on its own loop), so it is not
            // bounded here beyond the caller's ct.
            var renewRequestedAt = Stopwatch.GetTimestamp();
            HashSet<long>? live = [];
            try
            {
                foreach (var workerId in _context.WorkerIdByNamespace.Values)
                {
                    var extended = await _workers.ExtendWorkerLeasesAsync(workerId, _leaseTtlSeconds, _draining, ct);
                    live.UnionWith(extended);
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

            FeedJobLeases(snapshot, live, renewRequestedAt);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    // For each job this process was running as of the pre-extend snapshot: if an authoritative refresh
    // renewed it, feed its job-lease deadline forward; if the authoritative refresh dropped it (operator
    // cancel, or a stolen/reclaimed lease), cancel the attempt now. `live` is null when the refresh threw
    // (store unreachable): a definitive gone can't be told from a blip, so nothing is fed or cancelled here
    // - the deadline is left in place and the watchdog cancels only once it is about to lapse.
    private void FeedJobLeases(KeyValuePair<long, RunningAttempt>[] snapshot, HashSet<long>? live, long renewRequestedAt)
    {
        if (live is null)
        {
            return;
        }

        var goodUntil = renewRequestedAt + _ttlStopwatchTicks;
        foreach (var (jobId, attempt) in snapshot)
        {
            if (live.Contains(jobId))
            {
                // Confirmed renewal: the request-start is a lower bound on the store-stamped expiry.
                attempt.JobLeaseGoodUntil = goodUntil;
            }
            else
            {
                // Definitive loss under an authoritative refresh - operator cancel, or a stolen/reclaimed
                // lease. Stop it now rather than waiting out the watchdog.
                attempt.Cancel();
            }
        }
    }
}
