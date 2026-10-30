namespace Acta;

public abstract partial class JobContext
{
    private const int LockBackoffBaseMs = 50;
    private const int LockBackoffCapMs = 1000;
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="action"/> while holding a mutual-exclusion lock on <paramref name="key"/>, an opaque user
    /// key scoped to the job's namespace, so two namespaces hold the same key without contending. Acquisition is
    /// immediate-then-retry with exponential backoff until the <paramref name="timeout"/> budget is spent (null is
    /// about 30 s, <see cref="TimeSpan.Zero"/> one attempt); the lock is released when the action returns or throws.
    /// </summary>
    /// <exception cref="LockAcquisitionTimeoutException">The lock was not acquired within the budget.</exception>
    public Task RunWithLockAsync(string key, Func<Task> action, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunWithLockAsync(
            key,
            async () =>
            {
                await action();
                return true;
            },
            timeout,
            ct
        );
    }

    /// <summary>
    /// Result-returning overload of <see cref="RunWithLockAsync(string, Func{Task}, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    public async Task<TResult> RunWithLockAsync<TResult>(
        string key,
        Func<Task<TResult>> action,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(action);

        var budget = timeout ?? DefaultLockTimeout;
        if (budget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Lock acquisition timeout cannot be negative.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var lct = linked.Token;
        var deadlineTick = Environment.TickCount64 + (long)budget.TotalMilliseconds;
        var attempt = 0;

        while (true)
        {
            lct.ThrowIfCancellationRequested();

            if (await AcquireLockCoreAsync(key, lct) is { } token)
            {
                try
                {
                    return await action();
                }
                finally
                {
                    // Best-effort release: version-CAS makes it a no-op if the lease was already stolen.
                    // Use None so a cancelled action still releases. Cleanup failure must never replace
                    // the action outcome; the runtime reports it and the lease TTL provides recovery.
                    try
                    {
                        await ReleaseLockCoreAsync(key, token, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            OnLockReleaseFailure(key, ex);
                        }
                        catch
                        {
                            // Observability is best-effort too; a broken observer cannot alter the action outcome.
                        }
                    }
                }
            }

            var remainingMs = deadlineTick - Environment.TickCount64;
            if (budget == TimeSpan.Zero || remainingMs <= 0)
            {
                throw new LockAcquisitionTimeoutException(key, budget);
            }

            await Task.Delay(NextLockBackoffMs(attempt++, remainingMs), lct);
        }
    }

    /// <summary>Exponential (base doubled per attempt), capped, full-jittered, clamped to the remaining budget.</summary>
    private static int NextLockBackoffMs(int attempt, long remainingMs)
    {
        var exp = LockBackoffBaseMs * (1L << Math.Min(attempt, 20));
        var capped = (int)Math.Min(exp, LockBackoffCapMs);
        var jittered = Random.Shared.Next(capped / 2, capped + 1);
        return (int)Math.Clamp(jittered, 1, remainingMs);
    }

    /// <summary>
    /// Subclass sink: a single no-wait acquire attempt for <paramref name="key"/>. Returns the per-hold token on
    /// success, or <c>null</c> when the lock is busy.
    /// </summary>
    private protected abstract Task<Guid?> AcquireLockCoreAsync(string key, CancellationToken ct);

    /// <summary>Subclass sink: release the lock held under <paramref name="holdToken"/> for <paramref name="key"/>.</summary>
    private protected abstract Task ReleaseLockCoreAsync(string key, Guid holdToken, CancellationToken ct);

    /// <summary>
    /// Optional runtime observability hook for a best-effort release failure. Implementations must not
    /// throw; the wrapper defensively suppresses observer failures to preserve the handler outcome.
    /// </summary>
    private protected virtual void OnLockReleaseFailure(string key, Exception exception) { }
}
