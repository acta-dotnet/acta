using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A blocking handler gated per attempt of the same job, so a test can hold a stale reclaimed
/// attempt and its in-process replacement at the same time and release them independently.
/// </summary>
public static class AttemptOverlapProbe
{
    private sealed class Gate
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly ConcurrentDictionary<(long JobId, int Attempt), Gate> Gates = new();
    private static readonly ConcurrentDictionary<long, int> Attempts = new();

    public static void Reset(long jobId)
    {
        Attempts.TryRemove(jobId, out _);
        foreach (var key in Gates.Keys.Where(k => k.JobId == jobId))
        {
            Gates.TryRemove(key, out _);
        }
    }

    /// <summary>Completes once the given attempt of the job is inside the handler.</summary>
    public static Task Started(long jobId, int attempt) => Gates.GetOrAdd((jobId, attempt), static _ => new Gate()).Started.Task;

    /// <summary>Completes once the given attempt observed cancellation while blocked.</summary>
    public static Task Cancelled(long jobId, int attempt) => Gates.GetOrAdd((jobId, attempt), static _ => new Gate()).Cancelled.Task;

    /// <summary>Let the given attempt exit the handler normally.</summary>
    public static void Release(long jobId, int attempt) => Gates.GetOrAdd((jobId, attempt), static _ => new Gate()).Release.TrySetResult();

    // Generous ExecutionTimeout so the per-attempt timeout can never fire the Cancelled gate and
    // mask a missing heartbeat cancel; the test releases every attempt it starts.
    [Job("attempt-overlap", AuditLevel = JobAuditLevelCode.Audit, ExecutionTimeout = "PT2M")]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        var attempt = Attempts.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1);
        var gate = Gates.GetOrAdd((ctx.JobId, attempt), static _ => new Gate());
        gate.Started.TrySetResult();
        try
        {
            await gate.Release.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            gate.Cancelled.TrySetResult();
            throw;
        }
    }
}
