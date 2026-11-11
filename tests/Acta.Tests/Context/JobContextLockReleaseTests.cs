using Xunit;

namespace Acta.Tests.Context;

public sealed class JobContextLockReleaseTests
{
    [Fact]
    public async Task Release_failure_does_not_replace_successful_action_result()
    {
        var releaseFailure = new TimeoutException("release failed");
        var ctx = new RecordingJobContext { LockReleaseException = releaseFailure };

        var result = await ctx.RunWithLockAsync("key", () => Task.FromResult(42), ct: TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
        Assert.Equal(1, ctx.LockReleaseCalls);
        Assert.Same(releaseFailure, Assert.Single(ctx.LockReleaseFailures));
    }

    [Fact]
    public async Task Release_failure_does_not_mask_action_failure()
    {
        var actionFailure = new InvalidOperationException("handler failed");
        var ctx = new RecordingJobContext { LockReleaseException = new TimeoutException("release failed") };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.RunWithLockAsync<int>("key", () => Task.FromException<int>(actionFailure), ct: TestContext.Current.CancellationToken)
        );

        Assert.Same(actionFailure, thrown);
        Assert.Equal(1, ctx.LockReleaseCalls);
        Assert.Single(ctx.LockReleaseFailures);
    }

    [Fact]
    public async Task A_hold_answered_too_late_runs_no_action_and_is_released()
    {
        // The acquire's answer arrived past the hold's runway, so tracking it stopped the attempt: another holder may
        // already have the key, and the action must not run under it.
        using var attempt = new CancellationTokenSource();
        var ctx = new RecordingJobContext { Attempt = attempt, AcquireStopsAttempt = true };
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.RunWithLockAsync(
                "key",
                () =>
                {
                    ran = true;
                    return Task.CompletedTask;
                },
                ct: TestContext.Current.CancellationToken
            )
        );

        Assert.False(ran);
        Assert.Equal(1, ctx.LockReleaseCalls);
    }

    [Fact]
    public async Task Release_failure_does_not_mask_action_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ctx = new RecordingJobContext { LockReleaseException = new TimeoutException("release failed") };

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.RunWithLockAsync<int>("key", () => Task.FromCanceled<int>(cts.Token), ct: TestContext.Current.CancellationToken)
        );

        Assert.Equal(cts.Token, thrown.CancellationToken);
        Assert.Equal(1, ctx.LockReleaseCalls);
        Assert.Single(ctx.LockReleaseFailures);
    }
}
