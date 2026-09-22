using System.Runtime.ExceptionServices;
using Acta.Runtime.Kernel;
using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// Repeats the one write an attempt cannot afford to lose until it settles or the worker stops. The
/// heartbeat renews every row this worker leases from database state alone, so a completion that is
/// abandoned leaves the row Executing under a lease that never lapses and a recovery that never
/// reclaims it; a process restart or an operator cancel frees it, nothing else. The write is a
/// compare-and-swap on the attempt, so repeating it is safe; a row someone else moved reports that
/// rather than double-completing. Every failure is repeated, not only a provider error: a defect that
/// loops loudly in the log costs less than a row nothing can reclaim. Cancelling <c>ct</c> is the one
/// way out, which is why callers pass a token that outlives the attempt rather than the attempt's own.
/// The caller learns whether a retry happened, because a write whose first try committed and then
/// failed on the way back reports the lease as already cleared on the second, and that is a settled
/// write, not a loss.
/// </summary>
internal static class CompletionWrite
{
    // One second doubling to a thirty-second ceiling: a dropped connection costs almost nothing, and a
    // failover measured in minutes is ridden out without hammering the provider that is coming back.
    private static readonly TimeSpan DefaultFirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    // Warning while a blip is still the likely story, Error once the failure has outlived one.
    private const int WarningTries = 3;

    public static async Task<(T Result, bool Retried)> RetryAsync<T>(
        Func<CancellationToken, Task<T>> write,
        ILogger log,
        long jobId,
        CancellationToken ct,
        JobMetrics? metrics = null,
        TimeSpan? firstDelay = null
    )
    {
        var delay = firstDelay ?? DefaultFirstDelay;
        var counted = false;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return (await write(ct), attempt > 1);
                }
                // A cancelled token is the only exit that is not a settled write: the worker is stopping,
                // so whatever the write threw propagates as it is and the row falls to recovery.
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (!counted)
                    {
                        counted = true;
                        metrics?.RecordUnsettledCompletion(1);
                    }

                    log.Log(
                        attempt <= WarningTries ? LogLevel.Warning : LogLevel.Error,
                        ex,
                        "WorkerRuntime: completion write for job {JobId} failed on try {Count}; retrying in {DurationMs}ms until it lands or this worker stops.",
                        jobId,
                        attempt,
                        (long)delay.TotalMilliseconds
                    );

                    try
                    {
                        await Task.Delay(Jittered(delay), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopped mid-wait: report the write's own failure, not the stop. The stop says why
                        // the retry ended; the failure says why the row is still Executing.
                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }

                    var doubled = delay * 2;
                    delay = doubled > MaxDelay ? MaxDelay : doubled;
                }
            }
        }
        finally
        {
            if (counted)
            {
                metrics?.RecordUnsettledCompletion(-1);
            }
        }
    }

    // Up to a quarter of the wait again, so a fleet whose writes failed together does not come back in
    // lockstep and re-fail together. A zero delay (the test seam) stays exactly zero.
    private static TimeSpan Jittered(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? delay : delay + TimeSpan.FromTicks(Random.Shared.NextInt64((delay.Ticks / 4) + 1));
}
