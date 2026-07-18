using Acta;

namespace TestJobs;

/// <summary>A typed signal payload used by the signal conformance probes.</summary>
public sealed record ReviewDecision(bool Approved, string? Comment);

/// <summary>
/// Durable-signal probe handlers. Handlers restart from the top on every attempt, so each writes its
/// observable variables on the path it actually reaches; the wait returns once the slot is Set.
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

    [Job("job-wait-signal-typed")]
    public static async Task<ReviewDecision> WaitSignalTyped(JobContext ctx, CancellationToken ct)
    {
        var decision = await ctx.WaitSignalAsync<ReviewDecision>("review", ct);
        return decision!;
    }
}
