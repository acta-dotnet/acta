namespace Acta.Relational.Commands;

/// <summary>
/// Bounded retry for a store operation that the database aborted as a deadlock victim. The aborted
/// transaction is fully rolled back, so re-running the whole operation on a fresh connection is safe;
/// only a dialect-classified transient conflict retries, and a small randomized backoff staggers the
/// contenders so the retry does not immediately re-collide.
/// </summary>
internal static class DeadlockRetry
{
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        Func<Exception, bool> isTransient,
        int maxAttempts,
        CancellationToken ct,
        Func<Exception, bool>? isCancellation = null
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (attempt < maxAttempts && isTransient(ex))
            {
                await Task.Delay(BackoffMs(attempt), ct);
            }
            catch (Exception ex) when (ct.IsCancellationRequested && isCancellation?.Invoke(ex) is true)
            {
                // SqlClient surfaces a token-cancelled command as SqlException ("severe error on the
                // current command" / batch aborted 3980), not OperationCanceledException. Translate
                // only when the caller's token really is cancelled: a genuine severe error arriving
                // while a token happens to be cancelled must still surface as itself.
                throw new OperationCanceledException("The provider aborted the command because the caller's token was cancelled.", ex, ct);
            }
        }
    }

    // Linear base with jitter: scaled by attempt number so each retry waits longer, randomized so two
    // victims retrying together do not pick the same instant and deadlock again.
    private static int BackoffMs(int attempt) => Random.Shared.Next(4, 16) * attempt;
}
