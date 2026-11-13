using Acta;

namespace TestJobs;

/// <summary>Which child job each member of a group probe runs, in group order.</summary>
public sealed record ChildGroupStart(IReadOnlyList<string> ChildJobNames);

/// <summary>
/// The same shape for the unbounded twin. A distinct type because a manifest resolves a typed route
/// from the input alone, so two handlers cannot share one input record.
/// </summary>
public sealed record UnboundedChildGroupStart(IReadOnlyList<string> ChildJobNames);

/// <summary>Typed input routing to the parking child the group wrappers fan out to.</summary>
public sealed record GroupHold(string Label);

/// <summary>What a parent handler observed after a bounded group wait resolved.</summary>
public sealed record ChildGroupReport(bool TimedOut, bool Succeeded, IReadOnlyList<ChildOutcomeEntry> Children);

/// <summary>What a parent handler observed after two bounded group waits resolved in sequence.</summary>
public sealed record TwoGroupReport(ChildGroupReport First, ChildGroupReport Second);

/// <summary>
/// Bounded group-wait probes. Every group is armed far enough out that only a deliberate rewind of the
/// stored group deadline can expire it, so a timing flake cannot masquerade as a timeout. Each parent
/// writes a note after the wait, which appends rather than upserts and so counts resumptions exactly.
/// </summary>
public static class ChildGroupTimeoutProbes
{
    private static readonly TimeSpan LongWait = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A second, deliberately different bound for the two-group probe. Distinct durations make the two
    /// stored instants differ by construction, so a spec proving each group derived its own deadline
    /// does not have to rely on two clock readings happening to differ.
    /// </summary>
    private static readonly TimeSpan LongerWait = TimeSpan.FromMinutes(45);

    /// <summary>A child that parks on a signal the specs raise only when they want it to finish.</summary>
    [Job("job-child-group-hold")]
    public static async Task ChildGroupHold(GroupHold input, JobContext ctx, CancellationToken ct)
    {
        await ctx.SetVariableAsync("hold.label", input.Label, ct);
        await ctx.WaitSignalAsync("go", ct);
    }

    [Job("job-parent-join-children")]
    public static async Task<ChildGroupReport> ParentJoinChildren(ChildGroupStart input, JobContext ctx, CancellationToken ct)
    {
        var children = await StartGroupAsync(ctx, input.ChildJobNames, ct);
        var result = await ctx.JoinAsync(children, LongWait, ct);
        await ctx.NoteAsync("group wait resumed", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
        return Report(result);
    }

    /// <summary>
    /// The unbounded twin of the group probe, started from the same input, so a spec can pin that the
    /// bounded overload changed nothing about the shape the unbounded one produces.
    /// </summary>
    [Job("job-parent-join-children-unbounded")]
    public static async Task<ChildGroupReport> ParentJoinChildrenUnbounded(
        UnboundedChildGroupStart input,
        JobContext ctx,
        CancellationToken ct
    )
    {
        var children = await StartGroupAsync(ctx, input.ChildJobNames, ct);
        var result = await ctx.JoinAsync(children, ct);
        await ctx.NoteAsync("group wait resumed", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
        return Report(result);
    }

    /// <summary>
    /// Waits on a group holding one child that owns a subtree, beside a sibling child left out of the
    /// group, so the cascade's reach can be pinned in both directions: down through every unfinished
    /// group member, never into a child the group did not await.
    /// </summary>
    [Job("job-parent-join-children-subtree")]
    public static async Task<ChildGroupReport> ParentJoinChildrenSubtree(JobContext ctx, CancellationToken ct)
    {
        var deep = await ctx.StartChildAsync("deep", ctx.JobNamespace, "job-child-with-grandchild", JobPayload.None, ct: ct);
        var quick = await ctx.StartChildAsync("quick", ctx.JobNamespace, "job-child-quick", JobPayload.None, ct: ct);
        await ctx.StartChildAsync("sibling", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);

        var result = await ctx.JoinAsync([deep, quick], LongWait, ct);
        await ctx.NoteAsync("group wait resumed", ct);
        await ctx.SetVariableAsync("ran.after", true, ct);
        return Report(result);
    }

    /// <summary>
    /// Waits two bounded groups over disjoint children, one after the other, under deliberately
    /// different bounds. Each group derives its own deadline checkpoint, so the two budgets are independent
    /// and the second is computed only once the first group has resolved.
    /// </summary>
    [Job("job-parent-two-groups")]
    public static async Task<TwoGroupReport> ParentTwoGroups(JobContext ctx, CancellationToken ct)
    {
        var a0 = await ctx.StartChildAsync("a0", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);
        var a1 = await ctx.StartChildAsync("a1", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);
        var b0 = await ctx.StartChildAsync("b0", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);

        var first = await ctx.JoinAsync([a0, a1], LongWait, ct);
        var second = await ctx.JoinAsync([b0], LongerWait, ct);
        await ctx.NoteAsync("both groups resolved", ct);
        return new TwoGroupReport(Report(first), Report(second));
    }

    /// <summary>
    /// Waits the same children twice. The second wait derives the same checkpoint name and so reuses the
    /// deadline the first one stored, however stale it is by then; every member resolves off its own
    /// latch, which is what makes that harmless.
    /// </summary>
    [Job("job-parent-re-wait-same-group")]
    public static async Task<TwoGroupReport> ParentReWaitSameGroup(JobContext ctx, CancellationToken ct)
    {
        var c0 = await ctx.StartChildAsync("c0", ctx.JobNamespace, "job-wait-signal", JobPayload.None, ct: ct);

        var first = await ctx.JoinAsync([c0], LongWait, ct);
        var second = await ctx.JoinAsync([c0], LongWait, ct);
        await ctx.NoteAsync("re-wait resolved", ct);
        return new TwoGroupReport(Report(first), Report(second));
    }

    [Job("job-parent-execute-child-bounded")]
    public static async Task<ChildOutcomeEntry> ParentExecuteChildBounded(JobContext ctx, CancellationToken ct)
    {
        var outcome = await ctx.ExecuteChildAsync("only", new GroupHold("only"), LongWait, ct);
        await ctx.NoteAsync("execute child resumed", ct);
        return ChildOutcomeEntry.Of(outcome);
    }

    [Job("job-parent-parallel-bounded")]
    public static async Task<ChildGroupReport> ParentParallelBounded(JobContext ctx, CancellationToken ct)
    {
        var outcome = await ctx.ParallelAsync(
            "grp",
            branches => branches.Child("fast", new GroupHold("fast")).Child("slow", new GroupHold("slow")),
            LongWait,
            ct
        );
        await ctx.NoteAsync("parallel resumed", ct);
        return new ChildGroupReport(
            outcome.TimedOut,
            outcome.Succeeded,
            [ChildOutcomeEntry.Of(outcome.Branches["fast"]), ChildOutcomeEntry.Of(outcome.Branches["slow"])]
        );
    }

    [Job("job-parent-map-bounded")]
    public static async Task<ChildGroupReport> ParentMapBounded(JobContext ctx, CancellationToken ct)
    {
        var outcome = await ctx.MapAsync("grp", (string[])["fast", "slow"], key => key, key => new GroupHold(key), LongWait, ct);
        await ctx.NoteAsync("map resumed", ct);
        return new ChildGroupReport(outcome.TimedOut, outcome.Succeeded, [.. outcome.Items.Select(i => ChildOutcomeEntry.Of(i.Outcome))]);
    }

    private static async Task<EnqueueJobResponse[]> StartGroupAsync(
        JobContext ctx,
        IReadOnlyList<string> childJobNames,
        CancellationToken ct
    )
    {
        var children = new EnqueueJobResponse[childJobNames.Count];
        for (var i = 0; i < childJobNames.Count; i++)
        {
            children[i] = await ctx.StartChildAsync($"c{i}", ctx.JobNamespace, childJobNames[i], JobPayload.None, ct: ct);
        }
        return children;
    }

    private static ChildGroupReport Report(ChildrenWaitOutcome result) =>
        new(result.TimedOut, result.Succeeded, [.. result.Children.Select(ChildOutcomeEntry.Of)]);
}
