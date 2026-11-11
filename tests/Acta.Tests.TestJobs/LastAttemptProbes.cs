using System.Collections.Concurrent;
using Acta;

namespace TestJobs;

/// <summary>
/// <c>ctx.IsLastAttempt</c> probes: every attempt records what it read, keyed by job id and execution number, so a
/// spec can tell a resume that spent nothing from a retry that spent the budget.
/// </summary>
public static class LastAttemptProbes
{
    public static readonly ConcurrentDictionary<(long JobId, long ExecutionNumber), bool> Reads = new();

    // MaxAttempts = 2. The first attempt suspends on the signal, the resume after it fails, and the retry fails again.
    [Job("last-attempt-budget", MaxAttempts = 2, Backoff = "0s")]
    public static async Task Budget(JobContext ctx, CancellationToken ct)
    {
        Reads[(ctx.JobId, ctx.ExecutionNumber)] = ctx.IsLastAttempt;
        await ctx.WaitSignalAsync("go", ct);
        throw new InvalidOperationException("fails after the wait");
    }

    /// <summary>What a jittered attempt read, beside its context, whose planned retry the spec reads, keyed by job id.</summary>
    public static readonly ConcurrentDictionary<long, (bool Last, TimeSpan? UntilDeadline, JobContext Context)> JitterReads = new();

    // An hour of backoff jittered by half against a Strict deadline an hour out: a second, independent draw would decide
    // against the first about half the time.
    [Job("last-attempt-jitter", MaxAttempts = 5, Backoff = "1h..4h ~50%", Deadline = "1h")]
    public static Task Jitter(JobContext ctx, CancellationToken ct)
    {
        JitterReads[ctx.JobId] = (ctx.IsLastAttempt, ctx.TimeUntilDeadline, ctx);
        throw new InvalidOperationException("fails near the deadline");
    }

    // A Strict deadline an hour out and a fixed two-hour backoff: the first failure's retry would land past the deadline.
    [Job("last-attempt-deadline", MaxAttempts = 5, Backoff = "2h", Deadline = "1h")]
    public static Task Deadline(JobContext ctx, CancellationToken ct)
    {
        Reads[(ctx.JobId, ctx.ExecutionNumber)] = ctx.IsLastAttempt;
        throw new InvalidOperationException("fails before the deadline");
    }
}
