using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// Overload-resolution and forwarding tests for the positional-<c>ct</c> convenience overloads of
/// <c>StartChildAsync{TInput}</c> and <c>ExecuteChildAsync</c>: a bare <c>ct</c> in the third slot
/// must resolve to the new overload and behave identically to the named
/// <c>configure: null, ct: ct</c> form (same deduplication key, same parent, same outcome).
/// </summary>
public sealed class ChildAsyncOverloadTests
{
    private readonly record struct Probe;

    [Fact]
    public async Task StartChildAsync_positional_ct_forwards_configure_null_like_the_named_form()
    {
        var viaPositional = new RecordingJobContext();
        var viaNamed = new RecordingJobContext();

        await viaPositional.StartChildAsync("reserve-inventory", new Probe(), TestContext.Current.CancellationToken);
        await viaNamed.StartChildAsync("reserve-inventory", new Probe(), configure: null, ct: TestContext.Current.CancellationToken);

        var positional = Assert.Single(viaPositional.StartOptions);
        var named = Assert.Single(viaNamed.StartOptions);
        Assert.Equal("reserve-inventory", positional.DeduplicationKey);
        Assert.Equivalent(named, positional);
    }

    [Fact]
    public async Task ExecuteChildAsync_void_positional_ct_forwards_configure_null_and_reports_success()
    {
        var ctx = new RecordingJobContext();

        var outcome = await ctx.ExecuteChildAsync("reserve-inventory", new Probe(), TestContext.Current.CancellationToken);

        Assert.True(outcome.IsSuccess);
        var options = Assert.Single(ctx.StartOptions);
        Assert.Equal("reserve-inventory", options.DeduplicationKey);
        Assert.Equal(ctx.JobId, options.ParentJobId);
    }

    [Fact]
    public async Task ExecuteChildAsync_generic_positional_ct_forwards_configure_null_and_returns_the_seeded_result()
    {
        var ctx = new RecordingJobContext { SeededChildResult = 42 };

        var outcome = await ctx.ExecuteChildAsync<Probe, int>("reserve-inventory", new Probe(), TestContext.Current.CancellationToken);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(42, outcome.ValueOrThrow());
        var options = Assert.Single(ctx.StartOptions);
        Assert.Equal("reserve-inventory", options.DeduplicationKey);
    }

    [Fact]
    public async Task A_child_result_read_returns_the_result_or_throws_and_the_try_read_says_which()
    {
        var ct = TestContext.Current.CancellationToken;
        var stored = new RecordingJobContext { SeededChildResult = 0 };
        var none = new RecordingJobContext();

        // A stored zero is a result; a child that stored none is told apart by the Try read and refused by the other.
        Assert.Equal(0, await stored.GetChildResultAsync<int>(7, ct));
        Assert.Equal((true, 0), await stored.TryGetChildResultAsync<int>(7, ct));
        Assert.Equal((false, 0), await none.TryGetChildResultAsync<int>(7, ct));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => none.GetChildResultAsync<int>(7, ct));
        Assert.Contains("Child job 7 has stored no result", refused.Message, StringComparison.Ordinal);
    }
}
