using Acta.Relational.Commands;
using Xunit;

namespace Acta.Tests.Storage;

/// <summary>
/// Unit tests for <see cref="DeadlockRetry"/>. A deadlock victim is fully rolled back, so the store
/// re-runs the whole operation a bounded number of times; only the dialect-classified transient
/// conflict retries, everything else propagates on the first throw.
/// </summary>
public class DeadlockRetryTests
{
    private sealed class TransientException : Exception { }

    private static bool IsTransient(Exception ex) => ex is TransientException;

    [Fact]
    public async Task Returns_result_without_retry_on_first_success()
    {
        var attempts = 0;
        var result = await DeadlockRetry.RunAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(42);
            },
            IsTransient,
            maxAttempts: 5,
            CancellationToken.None
        );

        Assert.Equal(42, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var attempts = 0;
        var result = await DeadlockRetry.RunAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new TransientException();
                }
                return Task.FromResult("ok");
            },
            IsTransient,
            maxAttempts: 5,
            CancellationToken.None
        );

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Rethrows_after_exhausting_attempts()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<TransientException>(() =>
            DeadlockRetry.RunAsync<int>(
                _ =>
                {
                    attempts++;
                    throw new TransientException();
                },
                IsTransient,
                maxAttempts: 4,
                CancellationToken.None
            )
        );

        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task Does_not_retry_non_transient_exception()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeadlockRetry.RunAsync<int>(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException();
                },
                IsTransient,
                maxAttempts: 5,
                CancellationToken.None
            )
        );

        Assert.Equal(1, attempts);
    }

    private sealed class ProviderCancelException : Exception { }

    private static bool IsCancellation(Exception ex) => ex is ProviderCancelException;

    [Fact]
    public async Task Translates_cancellation_shaped_exception_when_token_is_cancelled()
    {
        // SqlClient surfaces a token-cancelled command as SqlException ("severe error" / batch
        // aborted), not OperationCanceledException; the funnel translates so callers see a normal
        // cancellation instead of logging a fake provider failure.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeadlockRetry.RunAsync<int>(_ => throw new ProviderCancelException(), IsTransient, maxAttempts: 5, cts.Token, IsCancellation)
        );

        Assert.IsType<ProviderCancelException>(oce.InnerException);
    }

    [Fact]
    public async Task Does_not_translate_when_token_is_not_cancelled()
    {
        // A genuine severe error without caller cancellation must surface untouched.
        await Assert.ThrowsAsync<ProviderCancelException>(() =>
            DeadlockRetry.RunAsync<int>(
                _ => throw new ProviderCancelException(),
                IsTransient,
                maxAttempts: 5,
                CancellationToken.None,
                IsCancellation
            )
        );
    }

    [Fact]
    public async Task Does_not_translate_unshaped_exceptions_under_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeadlockRetry.RunAsync<int>(_ => throw new InvalidOperationException(), IsTransient, maxAttempts: 5, cts.Token, IsCancellation)
        );
    }

    [Fact]
    public async Task Stops_retrying_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeadlockRetry.RunAsync<int>(
                _ =>
                {
                    attempts++;
                    cts.Cancel();
                    throw new TransientException();
                },
                IsTransient,
                maxAttempts: 5,
                cts.Token
            )
        );

        Assert.Equal(1, attempts);
    }
}
