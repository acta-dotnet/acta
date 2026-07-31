using Acta;

namespace TestJobs;

/// <summary>
/// Probe handlers for whole-job deadline conformance specs.
/// </summary>
public sealed class DeadlineProbes
{
    /// <summary>
    /// Strict 1s deadline. Records "ran" so the admission test can assert the handler did NOT run.
    /// </summary>
    [Job("deadline-strict-probe", Deadline = "PT1S")]
    public static async Task Strict(JobContext ctx, CancellationToken ct) => await ctx.SetVariableAsync("ran", true, ct);

    /// <summary>
    /// Advisory 1s deadline. Records ctx.IsOverdue; returns normally so the job lands Done.
    /// </summary>
    [Job("deadline-advisory-probe", Deadline = "PT1S", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
    public static async Task Advisory(JobContext ctx, CancellationToken ct) => await ctx.SetVariableAsync("overdue", ctx.IsOverdue, ct);

    /// <summary>
    /// Strict 1s deadline, 60s backoff, throws so a retry is attempted. The next retry (now+60s)
    /// overshoots the 1s deadline and the engine refuses to re-arm.
    /// </summary>
    [Job("deadline-retry-probe", Deadline = "1s", Backoff = "1m..8h x2 ±10%", MaxAttempts = 5)]
    public static Task Retry(JobContext ctx, CancellationToken ct) => throw new InvalidOperationException("boom");
}
