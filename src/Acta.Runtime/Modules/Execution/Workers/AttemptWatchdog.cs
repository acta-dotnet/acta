using System.Diagnostics;
using Acta.Runtime.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Enforces the per-attempt lease deadlines the renewers feed. Runs on its own loop and does no I/O - only
/// reads the monotonic deadlines on <see cref="RunningAttempt"/> - so a hung worker- or lock-store call can
/// never delay it (unlike an enforcement check inline in a renewal tick). Each tick it cancels any attempt
/// whose earliest lease (job lease or a held lock) is within the unwind margin of lapsing, so side effects
/// stop before another worker can reclaim the job or re-acquire the lock. The fail-safe half of the bounded
/// fail-open: a lone blip is tolerated (one missed feed still leaves most of the TTL), a real outage is not
/// (the deadline never advances through it). Backend-independent - it measures deadlines, not stores.
/// </summary>
internal sealed class AttemptWatchdog
{
    private readonly WorkerRegistration? _workerRegistration;
    private readonly WorkerContext _context;

    // Scan cadence: one quarter heartbeat interval. At the tightest validated config (LeaseTtl = 2x
    // HeartbeatInterval), the unwind margin is half an interval, so even a threshold crossed immediately
    // after one scan is observed with at least a quarter interval left before expiry.
    private readonly TimeSpan _cadence;

    // Unwind margin as monotonic Stopwatch ticks: cancel this far before a conservative deadline. Set to
    // half the base runway (LeaseTtl - HeartbeatInterval), which is always strictly less than the runway a
    // healthy attempt still has just before each renewal, so a renewer that keeps up never trips it - for
    // every config the validator permits (LeaseTtl >= 2x HeartbeatInterval), not only the 4x default. The
    // remaining half-runway is the handler's window to observe cancellation and unwind before the lease
    // actually lapses (a full interval at the 4x default). The faster scan preserves a quarter-interval
    // worst-case window even at the 2x floor.
    private readonly long _marginStopwatchTicks;
    private readonly Func<long> _getTimestamp;
    private readonly ILogger _log;

    public AttemptWatchdog(
        IOptions<JobsOptions> options,
        WorkerRegistration? workerRegistration,
        WorkerContext context,
        ILogger log,
        Func<long>? getTimestamp = null
    )
    {
        _workerRegistration = workerRegistration;
        _context = context;
        var interval = options.Value.HeartbeatInterval;
        _cadence = TimeSpan.FromTicks(Math.Max(1, interval.Ticks / 4));
        var marginSeconds = (options.Value.LeaseTtlSeconds - interval.TotalSeconds) / 2.0;
        _marginStopwatchTicks = (long)(marginSeconds * Stopwatch.Frequency);
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        _log.LogInformation("WorkerRuntime: starting lease watchdog loop (cadence {Cadence}).", _cadence);

        try
        {
            using var timer = new PeriodicTimer(_cadence);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await TickAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogError(ex, "WorkerRuntime: lease watchdog tick failed; retrying next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    // One watchdog pass: cancel every attempt whose earliest lease deadline is within the unwind margin.
    // Pure in-memory (no store calls), so it always runs to completion on time. Exposed for the test seam
    // and returns a completed task so RunHeartbeatOnceAsync can await the three passes uniformly.
    public Task TickAsync(CancellationToken ct)
    {
        var now = _getTimestamp();
        foreach (var (jobId, attempt) in _context.RunningAttempts)
        {
            if (attempt.EarliestLeaseGoodUntil() - now <= _marginStopwatchTicks)
            {
                attempt.Cancel();
                _log.LogWarning(
                    "WorkerRuntime: cancelling job {JobId}; lease renewal is failing and a lease is within the unwind margin of lapsing.",
                    jobId
                );
            }
        }
        return Task.CompletedTask;
    }
}
