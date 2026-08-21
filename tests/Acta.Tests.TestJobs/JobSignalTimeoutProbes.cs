using Acta;

namespace TestJobs;

/// <summary>What a Try-overload handler observed after its bounded wait resolved.</summary>
public sealed record WaitTimeoutReport(bool TimedOut, bool Received, string? Comment);

/// <summary>
/// Bounded durable-signal probes. Every wait is armed far enough out that only a deliberate rewind of
/// the stored expiration can expire it, so a timing flake cannot masquerade as a timeout. Each handler
/// writes a note after the wait, which appends rather than upserts and so counts resumptions exactly.
/// </summary>
public static class JobSignalTimeoutProbes
{
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(30);

    [Job("job-wait-signal-timeout")]
    public static async Task WaitWithTimeout(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        await ctx.WaitSignalAsync("go", LongWait, ct);
        await ctx.NoteAsync("wait resumed", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    [Job("job-wait-signal-timeout-typed")]
    public static async Task<WaitTimeoutReport> WaitWithTimeoutTyped(JobContext ctx, CancellationToken ct)
    {
        var decision = await ctx.WaitSignalAsync<ReviewDecision>("review", LongWait, ct);
        await ctx.NoteAsync("wait resumed", ct);
        return new WaitTimeoutReport(TimedOut: false, Received: true, decision?.Comment);
    }

    [Job("job-try-wait-signal-timeout")]
    public static async Task<WaitTimeoutReport> TryWaitWithTimeout(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        var result = await ctx.TryWaitSignalAsync("go", LongWait, ct);
        await ctx.NoteAsync("wait resumed", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
        return new WaitTimeoutReport(result.TimedOut, result.Received, Comment: null);
    }

    [Job("job-try-wait-signal-timeout-typed")]
    public static async Task<WaitTimeoutReport> TryWaitWithTimeoutTyped(JobContext ctx, CancellationToken ct)
    {
        var result = await ctx.TryWaitSignalAsync<ReviewDecision>("review", LongWait, ct);
        await ctx.NoteAsync("wait resumed", ct);
        return new WaitTimeoutReport(result.TimedOut, result.Received, result.Value?.Comment);
    }

    /// <summary>
    /// Times out on one signal and then parks on a second, unbounded one, which is the only shape that
    /// leaves a live job holding an already-expired slot: exactly what a late raise must not revive.
    /// </summary>
    [Job("job-try-wait-signal-timeout-then-hold")]
    public static async Task TryWaitWithTimeoutThenHold(JobContext ctx, CancellationToken ct)
    {
        var result = await ctx.TryWaitSignalAsync("go", LongWait, ct);
        await ctx.SetVariableAsync("go.timed-out", result.TimedOut, ct);
        await ctx.WaitSignalAsync("hold", ct);
        await ctx.NoteAsync("released", ct);
    }

    /// <summary>
    /// Times out on a bounded wait, parks, and on the replay after that waits on the SAME name with
    /// the unbounded overload. Policy is code, so the unbounded overload resolves TimedOut over the
    /// Expired slot too and takes the cancelling path rather than parking forever.
    /// </summary>
    [Job("job-try-wait-signal-timeout-then-unbounded")]
    public static async Task TryWaitWithTimeoutThenUnbounded(JobContext ctx, CancellationToken ct)
    {
        if (await ctx.ExistsVariableAsync("go.timed-out", ct))
        {
            await ctx.WaitSignalAsync("go", ct);
            await ctx.NoteAsync("unbounded wait resumed", ct);
            return;
        }

        var result = await ctx.TryWaitSignalAsync("go", LongWait, ct);
        await ctx.SetVariableAsync("go.timed-out", result.TimedOut, ct);
        await ctx.WaitSignalAsync("hold", ct);
    }

    /// <summary>
    /// Asks for a longer wait on every replay. The slot already exists by then, so the stored
    /// expiration must stay where the first attempt put it: state wins over code.
    /// </summary>
    [Job("job-wait-signal-timeout-replay")]
    public static async Task WaitWithGrowingTimeout(JobContext ctx, CancellationToken ct)
    {
        await ctx.WaitSignalAsync("go", ctx.ExecutionNumber == 1 ? LongWait : LongWait + LongWait, ct);
        await ctx.NoteAsync("wait resumed", ct);
    }

    /// <summary>
    /// Waits unbounded first and bounded on every replay, standing in for code redeployed with a bound
    /// over a job already suspended without one. The replay must arm the deadline the slot lacks.
    /// </summary>
    [Job("job-wait-signal-timeout-upgrade")]
    public static async Task WaitUnboundedThenBounded(JobContext ctx, CancellationToken ct)
    {
        if (ctx.ExecutionNumber == 1)
        {
            await ctx.WaitSignalAsync("go", ct);
        }
        else
        {
            await ctx.WaitSignalAsync("go", LongWait, ct);
        }
        await ctx.NoteAsync("wait resumed", ct);
    }

    /// <summary>
    /// The mirror of the upgrade probe: bounded first, unbounded on every replay. An unbounded re-entry
    /// must not clear a deadline the slot already carries.
    /// </summary>
    [Job("job-wait-signal-timeout-downgrade")]
    public static async Task WaitBoundedThenUnbounded(JobContext ctx, CancellationToken ct)
    {
        if (ctx.ExecutionNumber == 1)
        {
            await ctx.WaitSignalAsync("go", LongWait, ct);
        }
        else
        {
            await ctx.WaitSignalAsync("go", ct);
        }
        await ctx.NoteAsync("wait resumed", ct);
    }
}
