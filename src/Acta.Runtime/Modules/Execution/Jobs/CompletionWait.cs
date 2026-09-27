using System.Diagnostics;
using Acta.Runtime.Modules.Execution.Workers;

namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// The enqueue-and-wait loop: re-read the job until it is terminal or the wait budget runs out, sleeping
/// on its completion channel between reads. The next sleep is registered before each read, so a
/// completion that commits while the read is in flight still wakes it; registered after the read, that
/// wake would find no waiter, and a job channel keeps no latch without one.
/// </summary>
internal static class CompletionWait
{
    public static async ValueTask<(T? Snapshot, bool TimedOut)> AwaitAsync<T>(
        IWorkerWakeup wakeup,
        long jobId,
        Func<CancellationToken, ValueTask<T?>> read,
        Func<T, bool> isTerminal,
        TimeSpan waitTimeout,
        TimeSpan pollInterval,
        CancellationToken ct
    )
        where T : class
    {
        var channel = WorkerWakeupChannel.JobCompletion(jobId);
        var start = Stopwatch.GetTimestamp();
        T? last = null;

        while (true)
        {
            var remaining = waitTimeout - Stopwatch.GetElapsedTime(start);
            using var sleepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sleep =
                remaining > TimeSpan.Zero
                    ? wakeup.WaitAsync(channel, remaining < pollInterval ? remaining : pollInterval, sleepCts.Token).AsTask()
                    : null;

            T? snapshot;
            try
            {
                snapshot = await read(ct);
            }
            catch
            {
                await AbandonAsync(sleep, sleepCts);
                throw;
            }

            if (snapshot is not null)
            {
                last = snapshot;
                if (isTerminal(snapshot))
                {
                    await AbandonAsync(sleep, sleepCts);
                    return (snapshot, false);
                }
            }

            if (sleep is null)
            {
                return (last, true);
            }

            await sleep;
        }
    }

    // Cancels a sleep the loop no longer needs and waits for it to retire its waiter.
    private static async ValueTask AbandonAsync(Task<WorkerWakeupWaitStatus>? sleep, CancellationTokenSource sleepCts)
    {
        if (sleep is null)
        {
            return;
        }

        await sleepCts.CancelAsync();
        try
        {
            await sleep;
        }
        catch (OperationCanceledException) when (sleepCts.IsCancellationRequested)
        {
            // The sleep was abandoned, not the caller's wait.
        }
    }
}
