using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A handler that enters, then blocks until the test explicitly releases it, then returns normally. Unlike
/// <see cref="CancellableHandler"/> (which only unblocks on cancellation), this one runs to <c>completion</c>
/// when released - the shape a graceful drain needs, where a worker stops claiming but lets the in-flight
/// handler finish under the still-live host token. Per-namespace signals keep parallel runs isolated.
/// </summary>
public static class DrainGate
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> _release = new(StringComparer.Ordinal);

    public static void Reset(string jobNamespace)
    {
        _entered[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _release[jobNamespace] = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Completes once the handler has entered (the job is executing).</summary>
    public static Task Entered(string jobNamespace) => _entered[jobNamespace].Task;

    /// <summary>Lets the blocked handler return normally, so the job completes Succeeded.</summary>
    public static void Release(string jobNamespace) => _release[jobNamespace].TrySetResult();

    [Job("drain-gate")]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        _entered[ctx.JobNamespace].SetResult();
        // Waits for the test to release it; the linked attempt token still aborts a hard stop.
        await _release[ctx.JobNamespace].Task.WaitAsync(ct);
    }
}
