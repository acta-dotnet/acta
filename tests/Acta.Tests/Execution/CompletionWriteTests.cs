using System.Data.Common;
using Acta.Runtime.Modules.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Execution;

/// <summary>
/// The completion write's repeat. A completion abandoned after a thrown exception leaves a row the
/// heartbeat renews forever and recovery can never reclaim, so the write is repeated until it lands or
/// the worker stops, and a defect is repeated alongside a provider error because a loud loop costs less
/// than an unreclaimable row.
/// </summary>
public sealed class CompletionWriteTests
{
    [Fact(DisplayName = "A failure that clears is repeated until the write lands, and the caller is told a retry happened")]
    public async Task A_failure_that_clears_is_repeated_until_the_write_lands()
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

    [Fact(DisplayName = "A first try that lands reports no retry")]
    public async Task A_first_try_that_lands_reports_no_retry()
    {
        var (result, retried) = await CompletionWrite.RetryAsync<string>(
            _ => Task.FromResult("landed"),
            NullLogger.Instance,
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal("landed", result);
        Assert.False(retried);
    }

    // Fifty is past any bounded budget: the point is that no counter ends the repeat, and the old
    // five-try budget is the number this has to outlive.
    [Fact(DisplayName = "A provider error outlives any bounded budget and settles when it clears")]
    public async Task A_persistent_provider_error_outlives_a_bounded_budget()
    {
        var calls = 0;
        var (result, retried) = await CompletionWrite.RetryAsync<string>(
            _ =>
            {
                calls++;
                return calls < 50 ? throw new ProviderDown() : Task.FromResult("landed");
            },
            NullLogger.Instance,
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal("landed", result);
        Assert.True(retried);
        Assert.Equal(50, calls);
    }

    [Fact(DisplayName = "A failure that is not a provider error is repeated too, because abandoning it strands the row")]
    public async Task A_failure_that_is_not_a_provider_error_is_repeated_too()
    {
        var calls = 0;
        var (_, retried) = await CompletionWrite.RetryAsync<string>(
            _ =>
            {
                calls++;
                return calls < 4 ? throw new InvalidOperationException("a defect in the completion path") : Task.FromResult("landed");
            },
            NullLogger.Instance,
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.True(retried);
        Assert.Equal(4, calls);
    }

    [Fact(DisplayName = "A cancelled worker stops on the first try")]
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

    [Fact(DisplayName = "A worker that stops mid-repeat surfaces the write's own failure, not the stop")]
    public async Task A_worker_that_stops_mid_repeat_surfaces_the_write_failure()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        // The stop says why the repeat ended; the failure says why the row is still Executing, and that
        // is what the caller logs.
        await Assert.ThrowsAsync<ProviderDown>(() =>
            CompletionWrite.RetryAsync<int>(
                _ =>
                {
                    calls++;
                    cts.Cancel();
                    throw new ProviderDown();
                },
                NullLogger.Instance,
                jobId: 7,
                cts.Token,
                firstDelay: TimeSpan.FromMinutes(1)
            )
        );

        Assert.Equal(1, calls);
    }

    [Fact(DisplayName = "A logger that throws while the repeat reports a failure does not end the repeat")]
    public async Task A_throwing_logger_does_not_end_the_repeat()
    {
        var calls = 0;
        var (result, retried) = await CompletionWrite.RetryAsync<string>(
            _ =>
            {
                calls++;
                return calls < 2 ? throw new ProviderDown() : Task.FromResult("landed");
            },
            new ThrowingLogger(),
            jobId: 7,
            TestContext.Current.CancellationToken,
            firstDelay: TimeSpan.Zero
        );

        Assert.Equal("landed", result);
        Assert.True(retried);
    }

    private sealed class ProviderDown() : DbException("connection dropped");

    // Stands in for any diagnostic sink that fails: the repeat's job is the row, not the log line.
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => throw new InvalidOperationException("the log sink is down");
    }
}
