using Acta;

namespace TestJobs;

/// <summary>Input for the child-echo probe; the child returns it doubled.</summary>
public sealed record ChildEcho(int Value);

/// <summary>Result of the child-echo probe.</summary>
public sealed record ChildEchoResult(int Doubled);

/// <summary>Input for the cross-namespace parent probe: where to start the child.</summary>
public sealed record CrossNamespaceStart(string ChildNamespace);

/// <summary>
/// Child-job probe handlers. Parents start named children and join on their outcome latches;
/// handlers restart from the top on every attempt, so each step is replay-safe by construction.
/// </summary>
public static class ChildJobProbes
{
    [Job("job-child-echo")]
    public static Task<ChildEchoResult> ChildEcho(ChildEcho input, CancellationToken ct) =>
        Task.FromResult(new ChildEchoResult(input.Value * 2));

    [Job("job-child-fail")]
    public static Task ChildFail(JobContext ctx, CancellationToken ct) => JobContext.FailAsync("child says no \"thanks\" äß", ct);

    [Job("job-parent-one")]
    public static async Task<ChildEchoResult> ParentOne(JobContext ctx, CancellationToken ct)
    {
        var outcome = await ctx.ExecuteChildAsync<ChildEcho, ChildEchoResult>("echo", new ChildEcho(21), ct: ct);
        return outcome.ValueOrThrow();
    }

    [Job("job-parent-of-failing-child")]
    public static async Task<ChildJobOutcome> ParentOfFailingChild(JobContext ctx, CancellationToken ct)
    {
        var child = await ctx.StartChildAsync("doomed", ctx.JobNamespace, "job-child-fail", JobPayload.None, ct: ct);
        return await ctx.WaitChildAsync(child.JobId, ct);
    }

    [Job("job-parent-many")]
    public static async Task<ChildEchoResult> ParentMany(JobContext ctx, CancellationToken ct)
    {
        var a = await ctx.StartChildAsync("a", new ChildEcho(1), ct: ct);
        var b = await ctx.StartChildAsync("b", new ChildEcho(2), ct: ct);
        var c = await ctx.StartChildAsync("c", new ChildEcho(3), ct: ct);
        var outcomes = await ctx.WaitChildrenAsync([a.JobId, b.JobId, c.JobId], ct);

        var sum = 0;
        foreach (var outcome in outcomes)
        {
            sum += (await ctx.GetChildResultAsync<ChildEchoResult>(outcome.ChildJobId, ct))!.Doubled;
        }
        return new ChildEchoResult(sum);
    }

    [Job("job-parent-cross-namespace")]
    public static async Task<ChildJobOutcome> ParentCrossNamespace(CrossNamespaceStart input, JobContext ctx, CancellationToken ct)
    {
        var child = await ctx.StartChildAsync("remote", input.ChildNamespace, "job-child-echo", JobPayload.Json(new ChildEcho(4)), ct: ct);
        return await ctx.WaitChildAsync(child.JobId, ct);
    }

    [Job("job-parent-fire-and-forget")]
    public static async Task ParentFireAndForget(JobContext ctx, CancellationToken ct)
    {
        await ctx.StartChildAsync("orphan", new ChildEcho(5), ct: ct);
    }

    [Job("job-parent-cancel-self")]
    public static async Task ParentCancelSelf(JobContext ctx, CancellationToken ct)
    {
        await ctx.StartChildAsync("held", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);
        await JobContext.CancelAsync("superseded", ct);
    }
}
