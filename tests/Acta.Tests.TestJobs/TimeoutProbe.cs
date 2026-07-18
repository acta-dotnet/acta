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
