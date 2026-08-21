using Xunit;

namespace Acta.Tests.Context;

/// <summary>
/// Call-site contract for the bounded wait overloads: the timeout is validated before any substrate
/// call, and the new core sink is additive, so a JobContext subclass that only implements the
/// unbounded sink keeps working (and simply never sees a timeout).
/// </summary>
public sealed class JobContextWaitTimeoutTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_timeout_is_rejected_before_any_store_call(int seconds)
    {
        // RecordingJobContext's wait sink only understands child-latch names, so reaching it at all
        // would throw something other than ArgumentOutOfRangeException: this asserts the order too.
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var timeout = TimeSpan.FromSeconds(seconds);

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitSignalAsync("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitSignalAsync<string>("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitSignalAsync("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitSignalAsync<string>("go", timeout, ct));
        Assert.Empty(ctx.Events);

        // Both non-positive cases say the same thing about the same parameter. Delegating the negative
        // one to DurationSyntax would have named it a "Delay", which is not what the caller passed.
        Assert.Equal("timeout", thrown.ParamName);
        Assert.Contains("Wait timeout must be positive.", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_subclass_implementing_only_the_unbounded_sink_still_serves_a_bounded_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();

        var result = await ctx.TryWaitSignalAsync("go", TimeSpan.FromMinutes(5), ct);

        Assert.True(result.Received);
        Assert.False(result.TimedOut);
        Assert.Equal(["wait:go"], ctx.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_bounded_child_wait_validates_its_arguments_before_any_store_call(int seconds)
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.StartChildAsync("only", new { }, ct: ct);

        var badTimeout = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ctx.TryWaitChildAsync(child.JobId, TimeSpan.FromSeconds(seconds), ct)
        );
        var badId = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitChildAsync(0, TimeSpan.FromMinutes(5), ct));

        // Each throw names the argument the caller actually got wrong, not whichever guard ran first.
        Assert.Equal("timeout", badTimeout.ParamName);
        Assert.Equal("childJobId", badId.ParamName);
        Assert.Equal(["start:only"], ctx.Events);
    }

    [Fact]
    public async Task A_child_landing_before_the_deadline_returns_its_outcome_and_cancels_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new RecordingJobContext();
        var child = await ctx.StartChildAsync("only", new { }, ct: ct);

        var result = await ctx.TryWaitChildAsync(child.JobId, TimeSpan.FromMinutes(5), ct);

        Assert.True(result.Completed);
        Assert.False(result.TimedOut);
        Assert.Equal(child.JobId, result.ChildJobId);
        Assert.Equal(JobStatusCode.Succeeded, result.Outcome!.Status);
        Assert.Equal(["start:only", "wait:only"], ctx.Events);
    }

    [Fact]
    public async Task A_timed_out_child_wait_cancels_the_child_before_it_returns()
    {
        var ct = TestContext.Current.CancellationToken;
        var ctx = new TimingOutChildContext();

        var result = await ctx.TryWaitChildAsync(7, TimeSpan.FromMinutes(5), ct);

        // The ordering is the point: the handler must never regain control while the subtree it gave
        // up on is still live.
        Assert.True(result.TimedOut);
        Assert.False(result.Completed);
        Assert.Equal(7, result.ChildJobId);
        Assert.Null(result.Outcome);
        Assert.Equal(["wait:sys.child.7", "cancel:7"], ctx.Events);
    }

    /// <summary>
    /// A context whose bounded wait always expires, so the resolution order the runtime relies on can
    /// be pinned without a database.
    /// </summary>
    private sealed class TimingOutChildContext : RecordingJobContext
    {
        protected override Task<SignalWaitOutcome> WaitSignalCoreAsync(
            string name,
            int? timeoutSeconds,
            bool resumeOnTimeout,
            CancellationToken ct
        )
        {
            Events.Add($"wait:{name}");
            return Task.FromResult(new SignalWaitOutcome(0, null, TimedOut: true));
        }

        protected override Task CancelTimedOutChildCoreAsync(long childJobId, CancellationToken ct)
        {
            Events.Add($"cancel:{childJobId}");
            return Task.CompletedTask;
        }
    }
}
