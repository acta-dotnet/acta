using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A handler that blocks on its <see cref="JobContext.CancellationToken"/> until cancelled, then records
/// that it observed cancellation. Per-namespace signals keep parallel test runs isolated. Used to prove
/// that an external cancel of a running job propagates to the handler's token.
/// </summary>
public static class CancellableHandler
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _observed = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace)
    {
        _started[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _observed[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes once the handler has entered (the job is executing).</summary>
    public static Task Started(string jobNamespace) => _started[jobNamespace].Task;

    /// <summary>Completes with <c>true</c> once the handler observed cancellation of its token.</summary>
    public static Task<bool> Observed(string jobNamespace) => _observed[jobNamespace].Task;

    [Job("cancellable")]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        _started[ctx.JobNamespace].SetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _observed[ctx.JobNamespace].SetResult(true);
            throw;
        }
    }
}
