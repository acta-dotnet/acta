using Acta;

namespace TestJobs;

/// <summary>
/// Records the JobRef the handler observed so a spec can compare it to the enqueue outcome.
/// </summary>
public static class JobRefProbe
{
    [Job("jobref-probe")]
    public static async Task Run(JobContext ctx, CancellationToken ct) => await ctx.SetVariableAsync("seen-ref", ctx.JobRef.ToString(), ct);
}
