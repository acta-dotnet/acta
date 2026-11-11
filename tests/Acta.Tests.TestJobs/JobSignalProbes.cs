using Acta;

namespace TestJobs;

/// <summary>A typed signal payload used by the signal conformance probes.</summary>
public sealed record ReviewDecision(bool Approved, string? Comment);

/// <summary>
/// Durable-signal probe handlers. Handlers restart from the top on every attempt, so each writes its
/// observable variables on the path it actually reaches; the wait returns once the checkpoint is Set.
/// </summary>
public static class JobSignalProbes
{
    [Job("job-wait-signal")]
    public static async Task WaitSignal(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        await ctx.WaitSignalAsync("go", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    // Resets on the way out of its wait, as a finally around a whole handler does.
    [Job("job-wait-signal-then-reset")]
    public static async Task WaitSignalThenReset(JobContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.WaitSignalAsync("go", ct);
        }
        finally
        {
            await ctx.ResetStateAsync(ct);
        }
    }

    [Job("job-wait-signal-typed")]
    public static async Task<ReviewDecision> WaitSignalTyped(JobContext ctx, CancellationToken ct)
    {
        var decision = await ctx.WaitSignalAsync<ReviewDecision>("review", ct);
        return decision!;
    }
}
