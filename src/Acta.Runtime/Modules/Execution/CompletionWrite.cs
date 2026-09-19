using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// Retries the one write an attempt cannot afford to lose. The heartbeat renews every row this worker
/// leases from database state alone, so a completion that throws once and is then abandoned leaves the
/// row Executing under a lease that never lapses and a recovery that never reclaims it. The write is a
/// compare-and-swap on the attempt, so repeating it is safe; a row someone else moved reports that
/// rather than double-completing. Only provider errors are repeated, and only a bounded number of
/// times, so an outage longer than the window surfaces as the exception it is. The caller learns
/// whether a retry happened, because a write whose first try committed and then failed on the way
/// back reports the lease as already cleared on the second, and that is a settled write, not a loss.
/// </summary>
internal static class CompletionWrite
{
    // Five tries with four doubling waits from one second: about fifteen seconds, long enough for a
    // failover or a dropped connection to come back and well inside the lease the heartbeat keeps
    // extending meanwhile.
    internal const int Attempts = 5;
    private static readonly TimeSpan DefaultFirstDelay = TimeSpan.FromSeconds(1);

    public static async Task<(T Result, bool Retried)> RetryAsync<T>(
        Func<CancellationToken, Task<T>> write,
        ILogger log,
        long jobId,
        CancellationToken ct,
        TimeSpan? firstDelay = null
    )
    {
        var delay = firstDelay ?? DefaultFirstDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return (await write(ct), attempt > 1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            // Provider errors only: a dropped connection, a failover, a command timeout. Anything else
            // thrown from the completion path is a defect, and repeating a defect changes nothing.
            catch (DbException ex) when (attempt < Attempts)
            {
                log.LogWarning(
                    ex,
                    "WorkerRuntime: completion write for job {JobId} failed on try {Count} of ({Detail}); retrying in {DurationMs}ms.",
                    jobId,
                    attempt,
                    Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    (long)delay.TotalMilliseconds
                );
                await Task.Delay(delay, ct);
                delay *= 2;
            }
        }
    }
}
