using System.Diagnostics.CodeAnalysis;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>Runs a best-effort worker lifecycle phase concurrently under a linked deadline.</summary>
internal static class WorkerShutdownPhase
{
    public static async Task<bool> RunAsync<T>(
        IReadOnlyCollection<T> items,
        Func<T, CancellationToken, Task> operation,
        TimeSpan timeout,
        Action<T, Exception> onFailure,
        CancellationToken shutdownToken
    )
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onFailure);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        if (items.Count == 0)
        {
            return true;
        }

        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        phaseCts.CancelAfter(timeout);
        var tasks = items.Select(item => RunOneAsync(item, operation, onFailure, phaseCts.Token)).ToArray();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(phaseCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (phaseCts.IsCancellationRequested)
        {
            // The provider may ignore cancellation. WaitAsync still bounds this shutdown phase; the
            // detached per-item wrapper observes and logs any eventual failure.
            return false;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort shutdown phase. The outer catch routes a per-item failure to the caller's onFailure "
            + "reporter instead of faulting the Task.WhenAll, which would abandon the other items' stamps "
            + "mid-shutdown. The inner bare catch is deliberately broad and deliberately silent: it guards the "
            + "reporter itself, because logging cannot be allowed to fault a best-effort shutdown phase."
    )]
    private static async Task RunOneAsync<T>(
        T item,
        Func<T, CancellationToken, Task> operation,
        Action<T, Exception> onFailure,
        CancellationToken ct
    )
    {
        try
        {
            await operation(item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The phase deadline is expected and reported once by the caller.
        }
        catch (Exception ex)
        {
            try
            {
                onFailure(item, ex);
            }
            catch
            {
                // Logging cannot be allowed to fault a best-effort shutdown phase.
            }
        }
    }
}
