using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// ParallelAsync sugar: validates the group, rejects duplicate branches, starts every branch before
/// waiting, derives stable group-scoped child names, and returns branch-keyed outcomes.
/// </summary>
public sealed class ParallelAsyncTests
{
    private readonly record struct Probe;

    [Fact]
    public async Task Parallel_starts_all_branches_before_waiting()
    {
        var ctx = new RecordingJobContext();

        await ctx.ParallelAsync(
            "checkout",
            p => p.Child("reserve-inventory", new Probe()).Child("charge-card", new Probe()).Child("fraud-check", new Probe()),
            TestContext.Current.CancellationToken
        );

        var firstWait = ctx.Events.FindIndex(e => e.StartsWith("wait:"));
        var lastStart = ctx.Events.FindLastIndex(e => e.StartsWith("start:"));
        Assert.Equal(3, ctx.Started.Count);
        Assert.True(lastStart < firstWait, "every branch must start before any wait");
    }

    [Fact]
    public async Task Parallel_derives_stable_group_scoped_child_names()
    {
        var ctx = new RecordingJobContext();

        await ctx.ParallelAsync(
            "checkout",
            p => p.Child("reserve-inventory", new Probe()).Child("charge-card", new Probe()),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(new[] { "checkout-reserve-inventory", "checkout-charge-card" }, ctx.Started.Select(s => s.Name));
    }

    [Fact]
    public async Task Parallel_rejects_duplicate_branch_names()
    {
        var ctx = new RecordingJobContext();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            ctx.ParallelAsync(
                "checkout",
                p => p.Child("charge-card", new Probe()).Child("charge-card", new Probe()),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Empty(ctx.Started);
    }

    [Fact]
    public async Task Parallel_rejects_invalid_group_name()
    {
        var ctx = new RecordingJobContext();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            ctx.ParallelAsync("Not Kebab", p => p.Child("charge-card", new Probe()), TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Parallel_returns_branch_keyed_outcomes()
    {
        var ctx = new RecordingJobContext(
            new Dictionary<string, ChildJobOutcome> { ["checkout-charge-card"] = new(0, JobStatusCode.Failed) }
        );

        var checkout = await ctx.ParallelAsync(
            "checkout",
            p => p.Child("reserve-inventory", new Probe()).Child("charge-card", new Probe()),
            TestContext.Current.CancellationToken
        );

        Assert.Equal("checkout", checkout.GroupName);
        Assert.True(checkout["reserve-inventory"].Succeeded);
        Assert.False(checkout["charge-card"].Succeeded);
        Assert.False(checkout.Succeeded);
        Assert.Equal("charge-card", Assert.Single(checkout.Failed).Key);
    }
}
