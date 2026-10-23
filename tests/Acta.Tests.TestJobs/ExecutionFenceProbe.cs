using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A handler gated per attempt right before one durable write, the receipt variable, so a spec can hold an attempt's
/// write while its job moves on to another execution, then release it into the execution fence and read what the
/// write met.
/// </summary>
public static class ExecutionFenceProbe
{
    public const string ReceiptName = "receipt";

    private sealed class Gate
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<Exception?> Wrote = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly ConcurrentDictionary<(long JobId, int ExecutionNumber), Gate> Gates = new();

    private static Gate For(long jobId, int executionNumber) => Gates.GetOrAdd((jobId, executionNumber), static _ => new Gate());

    /// <summary>Completes once the given execution of the job is inside the handler, before its write.</summary>
    public static Task Started(long jobId, int executionNumber) => For(jobId, executionNumber).Started.Task;

    /// <summary>Lets the given execution make its write.</summary>
    public static void Release(long jobId, int executionNumber) => For(jobId, executionNumber).Release.TrySetResult();

    /// <summary>What the given execution's write threw, or null when it landed.</summary>
    public static Task<Exception?> Wrote(long jobId, int executionNumber) => For(jobId, executionNumber).Wrote.Task;

    // A generous ExecutionTimeout, so the per-attempt timeout never fires while a spec holds an attempt at its gate.
    [Job("execution-fence-probe", EventRecording = EventRecordingCode.Audit, ExecutionTimeout = "PT2M")]
    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        var gate = For(ctx.JobId, ctx.ExecutionNumber);
        gate.Started.TrySetResult();
        await gate.Release.Task.WaitAsync(ct);
        try
        {
            await ctx.SetVariableAsync(ReceiptName, $"attempt-{ctx.ExecutionNumber}", ct);
        }
        catch (Exception ex)
        {
            gate.Wrote.TrySetResult(ex);
            throw;
        }

        gate.Wrote.TrySetResult(null);
    }
}
