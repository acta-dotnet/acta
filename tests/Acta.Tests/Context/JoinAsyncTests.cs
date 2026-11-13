using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// JoinAsync, the group wait: waits for every child, returns outcomes in caller order, never throws on a failed child,
/// refuses a bad argument before it waits on any child, and ThrowIfAnyFailed is the opt-in escalation.
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

        Assert.Equal([c.JobRef, a.JobRef, b.JobRef], joined.Children.Select(o => o.JobRef));
        Assert.Equal(3, ctx.Events.Count(e => e.StartsWith("wait:")));
        Assert.True(joined.Succeeded);
    }

    [Fact]
    public async Task Join_returns_failed_outcomes_without_throwing()
    {
        var ctx = new RecordingJobContext(new Dictionary<string, JobStatusCode> { ["charge-card"] = JobStatusCode.Failed });
        var a = await ctx.StartChildAsync("reserve-inventory", new Probe(), ct: TestContext.Current.CancellationToken);
        var b = await ctx.StartChildAsync("charge-card", new Probe(), ct: TestContext.Current.CancellationToken);

        var joined = await ctx.JoinAsync([a, b], TestContext.Current.CancellationToken);

        Assert.False(joined.Succeeded);
        var failed = Assert.Single(joined.Failed);
        Assert.Equal(b.JobId, failed.JobId);
        Assert.Equal(JobStatusCode.Failed, failed.Status);
    }

    [Fact]
    public async Task ThrowIfAnyFailed_throws_with_failed_outcomes()
    {
        var ctx = new RecordingJobContext(new Dictionary<string, JobStatusCode> { ["charge-card"] = JobStatusCode.Cancelled });
        var a = await ctx.StartChildAsync("reserve-inventory", new Probe(), ct: TestContext.Current.CancellationToken);
        var b = await ctx.StartChildAsync("charge-card", new Probe(), ct: TestContext.Current.CancellationToken);

        var joined = await ctx.JoinAsync([a, b], TestContext.Current.CancellationToken);

        var ex = Assert.Throws<ChildGroupException>(joined.ThrowIfAnyFailed);
        Assert.Equal(b.JobId, Assert.Single(ex.Failed).JobId);
        // The message names each child by its ref, never by its numeric id.
        Assert.Contains($"{b.JobRef}:Cancelled", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Join_validates_every_argument_before_any_store_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.StartChildAsync("only", new Probe(), ct: ct);
        var unstarted = new EnqueueJobResponse(0, JobRef.New(), EnqueueJobAction.Inserted);
        Func<IReadOnlyList<EnqueueJobResponse>, Task>[] joins =
        [
            children => ctx.JoinAsync(children, ct),
            children => ctx.JoinAsync(children, TimeSpan.FromMinutes(5), ct),
        ];

        foreach (var join in joins)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => join(null!));
            await Assert.ThrowsAsync<ArgumentNullException>(() => join([child, null!]));
            var badId = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => join([child, unstarted]));
            Assert.Equal("children", badId.ParamName);
        }
        var badTimeout = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.JoinAsync([child], TimeSpan.Zero, ct));

        // A bad child anywhere in the list stops either join before it arms a latch or stores a deadline, so a rejected
        // group never leaves a half-armed wait behind.
        Assert.Equal("timeout", badTimeout.ParamName);
        Assert.Equal(["start:only"], ctx.Events);
    }
}
