using System.Data.Common;
using Acta.Runtime.Modules.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// The completion write's retry: a provider error on the write is repeated a bounded number of
/// times, because a completion abandoned after one thrown exception leaves a row the heartbeat renews
/// forever; anything that is not a provider error is a defect and goes straight through.
/// </summary>
public sealed class CompletionWriteTests
{
    [Fact]
    public async Task A_provider_error_that_clears_is_repeated_until_the_write_lands()
    {
        var calls = 0;
        var (result, retried) = await CompletionWrite.RetryAsync<string>(
            _ =>
            {
                calls++;
                return calls < 3 ? throw new ProviderDown() : Task.FromResult("landed");
            },
            NullLogger.Instance,
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal("landed", result);
        Assert.True(retried);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_provider_error_that_persists_gives_up_after_the_bounded_tries()
    {
        var calls = 0;
        await Assert.ThrowsAsync<ProviderDown>(() =>
            CompletionWrite.RetryAsync<int>(
                _ =>
                {
                    calls++;
                    throw new ProviderDown();
                },
                NullLogger.Instance,
                jobId: 7,
                TestContext.Current.CancellationToken,
                firstDelay: TimeSpan.Zero
            )
        );

        Assert.Equal(CompletionWrite.Attempts, calls);
    }

    [Fact]
    public async Task A_failure_that_is_not_a_provider_error_is_not_repeated()
    {
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CompletionWrite.RetryAsync<int>(
                _ =>
                {
                    calls++;
                    throw new InvalidOperationException("a defect in the completion path");
                },
                NullLogger.Instance,
                jobId: 7,
                TestContext.Current.CancellationToken,
                firstDelay: TimeSpan.Zero
            )
        );

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_cancelled_worker_stops_on_the_first_try()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CompletionWrite.RetryAsync<int>(
                token =>
                {
                    calls++;
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(0);
                },
                NullLogger.Instance,
                jobId: 7,
                cts.Token,
                firstDelay: TimeSpan.Zero
            )
        );

        Assert.Equal(1, calls);
    }

    private sealed class ProviderDown() : DbException("connection dropped");
}
