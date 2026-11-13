using Acta;

namespace TestJobs;

/// <summary>Input for the child-echo probe; the child returns it doubled.</summary>
public sealed record ChildEcho(int Value);

/// <summary>Result of the child-echo probe.</summary>
public sealed record ChildEchoResult(int Doubled);

/// <summary>Input for the cross-namespace parent probe: where to start the child.</summary>
public sealed record CrossNamespaceStart(string ChildNamespace);

/// <summary>Input for the result-less child probe.</summary>
public sealed record ResultlessChild(int Ignored);

/// <summary>
/// One awaited child's outcome, flattened to plain fields so it survives the JSON round trip through the result store;
/// the computed flags are read inside the handler, where the framework type still carries them.
/// </summary>
public sealed record ChildOutcomeEntry(long ChildJobId, JobStatusCode Status, bool TimedOut, bool Succeeded)
{
    public static ChildOutcomeEntry Of(JobOutcome outcome) => new(outcome.JobId, outcome.Status, outcome.TimedOut, outcome.Succeeded);
}

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
    public static Task ChildFail(JobContext ctx, CancellationToken ct) => ctx.FailAsync("child says no \"thanks\" äß", ct);

    [Job("job-parent-one")]
    public static async Task<ChildEchoResult> ParentOne(JobContext ctx, CancellationToken ct)
    {
        var outcome = await ctx.ExecuteChildAsync<ChildEcho, ChildEchoResult>("echo", new ChildEcho(21), ct: ct);
        return outcome.ValueOrThrow();
    }

    /// <summary>Runs its child through the manifest's generated call, which names the child's job and its result type.</summary>
    [Job("job-parent-by-call")]
    public static async Task<ChildEchoResult> ParentByCall(JobContext ctx, CancellationToken ct) =>
        (await ctx.ExecuteChildAsync("echo", TestJobsManifest.JobChildEcho(new ChildEcho(21)), ct)).ValueOrThrow();

    [Job("job-child-resultless")]
    public static Task ChildResultless(ResultlessChild input, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Asks a result-less child for an int; one attempt, so the typed wait's throw fails the job.</summary>
    [Job("job-parent-of-resultless-child", MaxAttempts = 1)]
    public static async Task<int> ParentOfResultlessChild(JobContext ctx, CancellationToken ct) =>
        (await ctx.ExecuteChildAsync<ResultlessChild, int>("resultless", new ResultlessChild(0), ct)).ValueOrThrow();

    /// <summary>
    /// Waits on a child that answers "old" from its first execution and drops an oversize result from any later one; one
    /// attempt, so the typed wait's throw fails the job.
    /// </summary>
    [Job("job-parent-of-restarted-child", MaxAttempts = 1)]
    public static async Task<string> ParentOfRestartedChild(JobContext ctx, CancellationToken ct) =>
        (await ctx.ExecuteChildAsync<OldThenOversizeInput, string>("restarted", new OldThenOversizeInput(0), ct)).ValueOrThrow();

    [Job("job-parent-of-failing-child")]
    public static async Task<ChildOutcomeEntry> ParentOfFailingChild(JobContext ctx, CancellationToken ct)
    {
        var child = await ctx.StartChildAsync("doomed", ctx.JobNamespace, "job-child-fail", JobPayload.None, ct: ct);
        return ChildOutcomeEntry.Of(await ctx.WaitChildAsync(child, ct));
    }

    [Job("job-parent-many")]
    public static async Task<ChildEchoResult> ParentMany(JobContext ctx, CancellationToken ct)
    {
        var a = await ctx.StartChildAsync("a", new ChildEcho(1), ct: ct);
        var b = await ctx.StartChildAsync("b", new ChildEcho(2), ct: ct);
        var c = await ctx.StartChildAsync("c", new ChildEcho(3), ct: ct);
        var joined = await ctx.JoinAsync([a, b, c], ct);

        var sum = 0;
        foreach (var outcome in joined.Children)
        {
            sum += (await ctx.GetChildResultAsync<ChildEchoResult>(outcome.JobId, ct)).Doubled;
        }
        return new ChildEchoResult(sum);
    }

    [Job("job-parent-cross-namespace")]
    public static async Task<ChildOutcomeEntry> ParentCrossNamespace(CrossNamespaceStart input, JobContext ctx, CancellationToken ct)
    {
        var child = await ctx.StartChildAsync("remote", input.ChildNamespace, "job-child-echo", JobPayload.Json(new ChildEcho(4)), ct: ct);
        return ChildOutcomeEntry.Of(await ctx.WaitChildAsync(child, ct));
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
        await ctx.CancelAsync("superseded", ct);
    }
}
