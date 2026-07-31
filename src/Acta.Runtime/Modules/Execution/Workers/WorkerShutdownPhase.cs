namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>Runs a best-effort worker lifecycle phase concurrently under a linked deadline.</summary>
internal static class WorkerShutdownPhase
{
    public static async Task<bool> RunAsync<T>(
        IReadOnlyCollection<T> items,
        Func<T, CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken shutdownToken,
        Action<T, Exception> onFailure
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
        var tasks = items.Select(item => RunOneAsync(item, operation, phaseCts.Token, onFailure)).ToArray();
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

    private static async Task RunOneAsync<T>(
        T item,
        Func<T, CancellationToken, Task> operation,
        CancellationToken ct,
        Action<T, Exception> onFailure
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
