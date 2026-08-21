using Acta;

namespace TestJobs;

/// <summary>Which job a parameterized try-wait parent probe should start as its one child.</summary>
public sealed record TryWaitChildStart(string ChildJobName);

/// <summary>What a parent handler observed after its bounded child wait resolved.</summary>
public sealed record ChildWaitReport(bool TimedOut, bool Completed, long ChildJobId, ChildJobOutcome? Outcome);

/// <summary>
/// Bounded child-wait probes. Every wait is armed far enough out that only a deliberate rewind of the
/// stored expiration can expire it, so a timing flake cannot masquerade as a timeout. Each parent
/// writes a note after the wait, which appends rather than upserts and so counts resumptions exactly.
/// </summary>
public static class ChildTimeoutProbes
{
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(30);

    /// <summary>A child that succeeds on its first tick and needs no input.</summary>
    [Job("job-child-quick")]
    public static Task ChildQuick(JobContext ctx, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// A child that leaves a live grandchild behind it and then parks, so a parent timing out on it has
    /// a real subtree to cancel.
    /// </summary>
    [Job("job-child-with-grandchild")]
    public static async Task ChildWithGrandchild(JobContext ctx, CancellationToken ct)
    {
        await ctx.StartChildAsync("grand", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);
        await ctx.WaitSignalAsync("go", ct);
    }

    [Job("job-parent-try-wait-child")]
    public static async Task<ChildWaitReport> ParentTryWaitChild(TryWaitChildStart input, JobContext ctx, CancellationToken ct)
    {
        var child = await ctx.StartChildAsync("only", ctx.JobNamespace, input.ChildJobName, JobPayload.None, ct: ct);
        var result = await ctx.TryWaitChildAsync(child.JobId, LongWait, ct);
        await ctx.NoteAsync("child wait resumed", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
        return new ChildWaitReport(result.TimedOut, result.Completed, result.ChildJobId, result.Outcome);
    }

    /// <summary>
    /// Waits on one child that owns a subtree while a sibling child runs beside it, so the cascade's
    /// reach can be pinned in both directions: down through the awaited child, never sideways.
    /// </summary>
    [Job("job-parent-try-wait-child-subtree")]
    public static async Task<ChildWaitReport> ParentTryWaitChildSubtree(JobContext ctx, CancellationToken ct)
    {
        var slow = await ctx.StartChildAsync("slow", ctx.JobNamespace, "job-child-with-grandchild", JobPayload.None, ct: ct);
        await ctx.StartChildAsync("sibling", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);
        var result = await ctx.TryWaitChildAsync(slow.JobId, LongWait, ct);
        await ctx.NoteAsync("child wait resumed", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
        return new ChildWaitReport(result.TimedOut, result.Completed, result.ChildJobId, result.Outcome);
    }

    /// <summary>
    /// The compensation shape: a parent that outlives its timed-out child, starts a replacement and
    /// joins on that one instead. Replaying it re-enters the expired wait before it re-dedupes the
    /// replacement, which is what makes the cancel's idempotence observable.
    /// </summary>
    [Job("job-parent-try-wait-child-then-retry")]
    public static async Task<ChildWaitReport> ParentTryWaitChildThenRetry(JobContext ctx, CancellationToken ct)
    {
        var first = await ctx.StartChildAsync("first", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);
        var result = await ctx.TryWaitChildAsync(first.JobId, LongWait, ct);
        if (result.Completed)
        {
            return new ChildWaitReport(result.TimedOut, result.Completed, result.ChildJobId, result.Outcome);
        }

        var replacement = await ctx.StartChildAsync("second", ctx.JobNamespace, "job-child-quick", JobPayload.None, ct: ct);
        var landed = await ctx.WaitChildAsync(replacement.JobId, ct);
        await ctx.NoteAsync("replacement child joined", ct);
        return new ChildWaitReport(result.TimedOut, result.Completed, result.ChildJobId, landed);
    }
}
