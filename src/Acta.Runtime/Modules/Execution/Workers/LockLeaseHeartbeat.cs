using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Acta.Runtime.Hosting;
using Acta.Runtime.Services.Locks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Renews the handler-acquired locks (<c>RunWithLock</c> and the exclusive-key mutex) running attempts
/// hold, through the swappable <see cref="ILockStore"/>, on its own loop - separate from the worker
/// heartbeat because the lock store is a distinct failure domain (relational today, Redis tomorrow). Each
/// tick feeds a still-held lock's deadline on a confirmed extend and cancels the attempt when a still-held
/// lock is definitively lost (<see cref="ILockStore.ExtendAsync"/> returns <c>false</c>); an uncertain
/// extend (exception/timeout) feeds nothing, left to the <see cref="AttemptWatchdog"/>. A no-op in
/// enqueue-only mode.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The one disposable field is _tickGate, a SemaphoreSlim used purely as an async mutex around "
        + "TickAsync. A SemaphoreSlim only holds an OS wait handle once AvailableWaitHandle is read, and nothing in "
        + "Acta reads it, so Dispose() would release nothing. Disposal would also be unsafe at every point a "
        + "shutdown could pick it: the gate is entered by the lock-lease loop and by "
        + "WorkerRuntime.RunHeartbeatOnceAsync, so a disposed gate would turn a harmless late tick into "
        + "ObjectDisposedException. The type is created once per WorkerRuntime and lives as long as the process; "
        + "deliberately not disposable. The per-extend linked source inside TickAsync is separately 'using'-scoped."
)]
internal sealed class LockLeaseHeartbeat(
    ILockStore lockStore,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger log
)
{
    private readonly ILockStore _lockStore = lockStore;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly TimeSpan _interval = options.Value.HeartbeatInterval;
    private readonly TimeSpan _lockTtl = TimeSpan.FromSeconds(options.Value.LeaseTtlSeconds);
    private readonly long _ttlStopwatchTicks = (long)(options.Value.LeaseTtlSeconds * (double)Stopwatch.Frequency);
    private readonly ILogger _log = log;

    // Serializes TickAsync so the loop cannot race itself (double extends against one lock).
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    public async Task RunAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        _log.LogInformation("WorkerRuntime: starting lock-lease heartbeat loop (interval {Interval}).", _interval);

        try
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown landed before the first tick; the periodic wait below exits through the outer catch.
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "WorkerRuntime: initial lock-lease tick failed; retrying on the interval.");
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
                    _log.LogError(ex, "WorkerRuntime: lock-lease tick failed; retrying next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    // One lock-lease pass: extend every lock each running attempt holds, feeding a confirmed extend's
    // deadline forward and cancelling on a definitive loss. The deterministic single-shot the loop drives
    // per tick; tests drive it via WorkerRuntime.RunHeartbeatOnceAsync.
    public async Task TickAsync(CancellationToken ct)
    {
        await _tickGate.WaitAsync(ct);
        try
        {
            foreach (var (jobId, attempt) in _context.RunningAttempts)
            {
                foreach (var token in attempt.HeldLocks)
                {
                    // Capture the request-start BEFORE the extend: the store stamps the refreshed expiry no
                    // earlier than this instant, so requestStart + TTL is a conservative lower bound on it.
                    var requestedAt = Stopwatch.GetTimestamp();

                    // Bound the extend so a hung lock store (a stalled Redis connection) cannot hold the
                    // tick past a beat; a timeout surfaces as an OCE off callCts (ct not cancelled) and is
                    // handled exactly like a transient throw below.
                    using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    callCts.CancelAfter(_interval);
                    try
                    {
                        var stillHeld = await _lockStore.ExtendAsync(token, _lockTtl, callCts.Token);
                        if (stillHeld)
                        {
                            // Confirmed extend: advance this lock's deadline (no-op if the handler released
                            // and untracked it while the extend was in flight).
                            attempt.ExtendLock(token, requestedAt + _ttlStopwatchTicks);
                        }
                        else if (attempt.Holds(token))
                        {
                            // Definitive steal while the handler still relies on the lock: its critical
                            // section is no longer mutually exclusive, so stop the attempt. (A false for a
                            // lock the handler already released is untracked first, so Holds() is false.)
                            attempt.Cancel();
                            _log.LogWarning("WorkerRuntime: cancelling job {JobId}; a held lock was lost.", jobId);
                            break;
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        if (!attempt.Holds(token))
                        {
                            // The handler released and untracked the lock while this extend was in flight,
                            // so it no longer relies on the lease; not a renewal failure.
                            continue;
                        }
                        // Uncertain extend (transient fault or timeout): feed nothing and leave the lock's
                        // deadline for the watchdog to enforce if the outage outlasts it.
                        _log.LogWarning(
                            ex,
                            "WorkerRuntime: could not extend a held lock for job {JobId} this tick; leaving its deadline for the watchdog.",
                            jobId
                        );
                    }
                }
            }
        }
        finally
        {
            _tickGate.Release();
        }
    }
}
