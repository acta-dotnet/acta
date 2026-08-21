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

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitSignalAsync("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.WaitSignalAsync<string>("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitSignalAsync("go", timeout, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ctx.TryWaitSignalAsync<string>("go", timeout, ct));
        Assert.Empty(ctx.Events);
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
}
