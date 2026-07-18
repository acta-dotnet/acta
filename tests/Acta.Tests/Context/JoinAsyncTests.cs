using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// JoinAsync sugar over WaitChildrenAsync: waits for every handle, returns outcomes in caller order,
/// never throws on a failed child, and ThrowIfAnyFailed is the opt-in escalation.
/// </summary>
public sealed class JoinAsyncTests
{
    private readonly record struct Probe;

    [Fact]
    public async Task Join_waits_all_children_and_preserves_caller_order()
    {
        var ctx = new RecordingJobContext();
        var a = await ctx.StartChildAsync("reserve-inventory", new Probe(), ct: TestContext.Current.CancellationToken);
        var b = await ctx.StartChildAsync("charge-card", new Probe(), ct: TestContext.Current.CancellationToken);
        var c = await ctx.StartChildAsync("fraud-check", new Probe(), ct: TestContext.Current.CancellationToken);

        var joined = await ctx.JoinAsync([c, a, b], TestContext.Current.CancellationToken);

        Assert.Equal(new[] { c.JobId, a.JobId, b.JobId }, joined.Children.Select(o => o.ChildJobId));
        Assert.Equal(3, ctx.Events.Count(e => e.StartsWith("wait:")));
        Assert.True(joined.Succeeded);
    }

    [Fact]
    public async Task Join_returns_failed_outcomes_without_throwing()
    {
        var ctx = new RecordingJobContext(new Dictionary<string, ChildJobOutcome> { ["charge-card"] = new(0, JobStatusCode.Failed) });
        var a = await ctx.StartChildAsync("reserve-inventory", new Probe(), ct: TestContext.Current.CancellationToken);
        var b = await ctx.StartChildAsync("charge-card", new Probe(), ct: TestContext.Current.CancellationToken);

        var joined = await ctx.JoinAsync([a, b], TestContext.Current.CancellationToken);

        Assert.False(joined.Succeeded);
        var failed = Assert.Single(joined.Failed);
        Assert.Equal(b.JobId, failed.ChildJobId);
        Assert.Equal(JobStatusCode.Failed, failed.Status);
    }

    [Fact]
    public async Task ThrowIfAnyFailed_throws_with_failed_outcomes()
    {
        var ctx = new RecordingJobContext(new Dictionary<string, ChildJobOutcome> { ["charge-card"] = new(0, JobStatusCode.Cancelled) });
        var a = await ctx.StartChildAsync("reserve-inventory", new Probe(), ct: TestContext.Current.CancellationToken);
        var b = await ctx.StartChildAsync("charge-card", new Probe(), ct: TestContext.Current.CancellationToken);

        var joined = await ctx.JoinAsync([a, b], TestContext.Current.CancellationToken);

        var ex = Assert.Throws<ChildGroupException>(joined.ThrowIfAnyFailed);
        Assert.Equal(b.JobId, Assert.Single(ex.Failed).ChildJobId);
    }

    [Fact]
    public async Task ThrowIfAnyFailed_is_silent_when_all_succeed()
    {
        var ctx = new RecordingJobContext();
        var a = await ctx.StartChildAsync("reserve-inventory", new Probe(), ct: TestContext.Current.CancellationToken);
        var b = await ctx.StartChildAsync("charge-card", new Probe(), ct: TestContext.Current.CancellationToken);

        var joined = await ctx.JoinAsync([a, b], TestContext.Current.CancellationToken);

        joined.ThrowIfAnyFailed();
        Assert.True(joined.Succeeded);
    }
}
