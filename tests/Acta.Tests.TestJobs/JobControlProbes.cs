using Acta;

namespace TestJobs;

/// <summary>
/// Handler-initiated control probes: each writes <c>ran.before</c>, calls the control verb, then
/// attempts <c>ran.after</c> - so a spec can prove the verb does not return to user code.
/// </summary>
public static class JobControlProbes
{
    [Job("job-handler-fail")]
    public static async Task HandlerFail(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        await ctx.FailAsync("payload invalid", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    [Job("job-handler-cancel")]
    public static async Task HandlerCancel(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        await ctx.CancelAsync("duplicate request", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }

    [Job("job-throw-not-implemented")]
    public static async Task ThrowNotImplemented(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        throw new NotImplementedException("handler is a stub");
    }

    [Job("job-handler-pause")]
    public static async Task HandlerPause(JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("ran.before", true, ct);
        await ctx.PauseAsync("awaiting manual review", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
    }
}
