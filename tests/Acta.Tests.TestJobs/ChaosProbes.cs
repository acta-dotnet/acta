using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// Test-only handlers for hostile-timing conformance specs.
/// </summary>
public static class ChaosProbes
{
    private sealed class Gate
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly ConcurrentDictionary<long, Gate> Gates = new();

    public static readonly ConcurrentDictionary<long, int> CountingInvocations = new();

    public static void Reset(long jobId)
    {
        Gates[jobId] = new Gate();
        CountingInvocations.TryRemove(jobId, out _);
        JobStepProbes.BodyInvocations.TryRemove(jobId, out _);
    }

    public static Task WaitStartedAsync(long jobId, CancellationToken ct) =>
        Gates.GetOrAdd(jobId, static _ => new Gate()).Started.Task.WaitAsync(ct);

    public static Task WaitCancelledAsync(long jobId, CancellationToken ct) =>
        Gates.GetOrAdd(jobId, static _ => new Gate()).Cancelled.Task.WaitAsync(ct);

    public static void Release(long jobId) => Gates.GetOrAdd(jobId, static _ => new Gate()).Release.TrySetResult();

    [Job("chaos-counting", AuditLevel = JobAuditLevelCode.Audit)]
    public static Task Counting(JobContext ctx, CancellationToken ct)
    {
        CountingInvocations.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1);
        return Task.CompletedTask;
    }

    // Short ExecutionTimeout so a stolen-lease attempt that finalizes via the per-attempt timeout
    // caps at seconds, not the 5-minute framework default.
    [Job("chaos-blocking", AuditLevel = JobAuditLevelCode.Audit, ExecutionTimeout = "PT10S")]
    public static async Task Blocking(JobContext ctx, CancellationToken ct)
    {
        CountingInvocations.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1);
        var gate = Gates.GetOrAdd(ctx.JobId, static _ => new Gate());
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

    [Job("chaos-step-before-complete", AuditLevel = JobAuditLevelCode.Audit)]
    public static async Task<string> StepBeforeComplete(JobContext ctx, CancellationToken ct)
    {
        await ctx.RunStepAsync(
            "durable-side-effect",
            async _ =>
            {
                JobStepProbes.BodyInvocations.AddOrUpdate(ctx.JobId, 1, static (_, n) => n + 1);
                await Task.CompletedTask;
                return "ok";
            },
            ct: ct
        );

        return "ok";
    }
}
