using System.Runtime.ExceptionServices;
using Acta.Runtime.Kernel;
using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// Repeats the one write an attempt cannot afford to lose until it settles or the worker stops. The
/// heartbeat renews every row this worker leases from database state alone, so an abandoned completion
/// leaves the row Executing under a lease that never lapses; a restart or an operator cancel frees it,
/// nothing else. The write is a compare-and-swap on the attempt, so repeating it is safe, and every
/// failure is repeated, not only a provider error: a defect that loops loudly in the log costs less than
/// a row nothing can reclaim. The caller learns whether a retry happened, because a write that committed
/// and then failed on the way back reports the lease as already cleared on the second try, and that is
/// a settled write, not a loss. The token is the one way out, so it must be the worker's host token and
/// never the attempt's: an external cancel, the attempt deadline, and the watchdog all cancel the
/// attempt token, the last of them on the very lease-renewal outage that makes the write fail. A
/// graceful drain leaves the host token live, so completions keep landing while the shutdown budget
/// lasts; only a hard stop ends the repeat. The token bounds only the waits between tries, never the
/// store call itself.
/// </summary>
internal static class CompletionWrite
{
    // One second doubling to a thirty-second ceiling, on the runtime's one retry curve: a dropped
    // connection costs almost nothing, and a failover measured in minutes is ridden out without
    // hammering the provider that is coming back. A quarter of jitter keeps a fleet whose writes failed
    // together from coming back in lockstep.
    private static readonly Backoff DefaultBackoff = Backoff
        .Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
        .WithJitter(0.25);

    // Warning while a blip is still the likely story, Error once the failure has outlived one.
    private const int WarningTries = 3;

    /// <summary>The wait before try <paramref name="attempt"/> + 1 on the runtime's retry curve.</summary>
    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(BackoffSchedule.ComputeDelaySeconds(attempt, DefaultBackoff));

    public static async Task<(T Result, bool Retried)> RetryAsync<T>(
        Func<CancellationToken, Task<T>> write,
        ILogger log,
        long jobId,
        CancellationToken ct,
        JobMetrics? metrics = null,
        TimeSpan? firstDelay = null
    )
    {
        // A zero first delay is the test seam: the curve stays at zero however many tries it takes.
        var backoff = firstDelay is { } first
            ? Backoff.Exponential(first, first > DefaultBackoff.MaxDelay ? first : DefaultBackoff.MaxDelay)
            : DefaultBackoff;
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
                    var delay = TimeSpan.FromSeconds(BackoffSchedule.ComputeDelaySeconds(attempt, backoff));
                    // Diagnostics cannot be allowed to end the repeat: a metrics listener or a logger that
                    // throws would abandon the row the repeat exists to keep.
                    try
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
                    }
                    catch (Exception)
                    {
                        // Swallowed on purpose; the write's own failure is the one being handled.
                    }

                    try
                    {
                        await Task.Delay(delay, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopped mid-wait: report the write's own failure, not the stop. The stop says why
                        // the retry ended; the failure says why the row is still Executing.
                        ExceptionDispatchInfo.Capture(ex).Throw();
                    }
                }
            }
        }
        finally
        {
            if (counted)
            {
                try
                {
                    metrics?.RecordUnsettledCompletion(-1);
                }
                catch (Exception)
                {
                    // A listener that throws must not turn a settled write into a failure.
                }
            }
        }
    }
}
