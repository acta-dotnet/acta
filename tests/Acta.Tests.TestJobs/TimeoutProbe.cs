using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// A one-shot handler that blocks on its cancellation token forever. With a short
/// <see cref="JobAttribute.ExecutionTimeout"/> the per-attempt timeout cancels the token; the handler
/// unwinds and the completion records the timeout reason. <c>MaxAttempts = 1</c> so the timeout lands
/// terminal Failed rather than re-arming, keeping the assertion simple.
/// </summary>
public static class TimeoutProbe
{
    [Job("timeout-probe", ExecutionTimeout = "PT1S", MaxAttempts = 1)]
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
}

/// <summary>
/// A two-attempt handler that blocks on its cancellation token forever. With a short
/// <see cref="JobAttribute.ExecutionTimeout"/> each attempt times out: the first re-arms (budget
/// remaining, <c>failure_count</c> 0→1, status Ready) and the second terminates (budget exhausted,
/// <c>failure_count</c> 1→2, status Failed). Zero backoff so the re-armed attempt is immediately claimable.
/// </summary>
public static class TimeoutBudgetProbe
{
    [Job("timeout-budget-probe", ExecutionTimeout = "1s", MaxAttempts = 2, Backoff = "0s")]
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
}

/// <summary>
/// A handler that ignores its cancellation token: it waits for the spec's release, not for its token, and returns
/// <see cref="Result"/> normally. <see cref="TokenFired"/> completes once the one-second
/// <see cref="JobAttribute.ExecutionTimeout"/> has fired the running handler's token. <c>MaxAttempts = 1</c> lands an
/// attempt whose timeout fired before the handler started terminal Failed, never a second execution.
/// </summary>
public static class TimeoutIgnoredProbe
{
    /// <summary>What the handler returns.</summary>
    public const string Result = "returned-late";

    private sealed class Gate
    {
        public readonly TaskCompletionSource TokenFired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly ConcurrentDictionary<long, Gate> Gates = new();

    private static Gate For(long jobId) => Gates.GetOrAdd(jobId, static _ => new Gate());

    /// <summary>Completes once the job's handler is running and its token has fired.</summary>
    public static Task TokenFired(long jobId) => For(jobId).TokenFired.Task;

    /// <summary>Lets the job's handler return.</summary>
    public static void Release(long jobId) => For(jobId).Release.TrySetResult();

    [Job("timeout-ignored-probe", ExecutionTimeout = "1s", MaxAttempts = 1)]
    public static async Task<string> Run(JobContext ctx, CancellationToken ct)
    {
        var gate = For(ctx.JobId);
        using var fired = ct.Register(() => gate.TokenFired.TrySetResult());
        await gate.Release.Task;
        return Result;
    }
}
