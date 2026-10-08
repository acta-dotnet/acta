namespace Acta.Runtime;

/// <summary>
/// The locks the engine takes, behind <c>JobContext.RunWithLock</c> and the concurrency-slot
/// admission (<c>{ns_id}.sem.{key}.{slot}</c> rows taken by the runner after claim; a loser re-arms
/// Ready after the fixed bounce delay), each on the <c>locks</c> table. No-wait: a single attempt, so
/// the caller owns any retry/backoff.
/// </summary>
internal sealed class Locks(
    AcquireLockHandler acquireLock,
    AcquireSlotHandler acquireSlot,
    ReserveRateHandler reserveRate,
    ExtendLockHandler extendLock,
    ReleaseLockHandler releaseLock
)
{
    /// <summary>The token is null when the lock is currently held; carry it to ReleaseLockAsync.</summary>
    public Task<AcquireLockResponse> AcquireLockAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
        acquireLock.HandleAsync(new AcquireLockCommand(key, ttl, ownerJobId), ct);

    /// <summary>
    /// Takes the lowest-numbered free slot of a counted key (limit at least 1) in one round trip: the slots
    /// are the rows <c>{keyPrefix}.0</c> .. <c>{keyPrefix}.{limit-1}</c>, and a slot is free when it has no
    /// row or its hold expired (steal on expiry, as AcquireLockAsync does). The token is null when every slot is held, and
    /// also when a racer took the chosen slot first: the routine makes one attempt and never retries, so the
    /// caller settles the loss rather than waiting. The token carries the slot's own key for extend and release.
    /// </summary>
    public Task<AcquireSlotResponse> AcquireSlotAsync(string keyPrefix, int limit, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
        acquireSlot.HandleAsync(new AcquireSlotCommand(keyPrefix, limit, ttl, ownerJobId), ct);

    /// <summary>
    /// Spends one turn from the GCRA meter at <paramref name="bucketKey"/>: each request moves its arrival
    /// time one interval on, and an idle meter admits exactly <paramref name="burst"/> back to back.
    /// Admitted means run now; otherwise the turn is booked at the returned instant, measured on the
    /// store's clock so a caller sleeps or re-arms to it without racing the meter. The bucket lapses once
    /// an idle meter equals a missing one, and a reservation lapses <paramref name="graceSeconds"/> past
    /// its turn; a turn stale by more than a second (or one interval) is re-metered rather than released.
    /// </summary>
    public Task<ReserveRateResponse> ReserveRateAsync(
        string bucketKey,
        long jobId,
        int intervalMilliseconds,
        int burst,
        int graceSeconds,
        CancellationToken ct
    ) => reserveRate.HandleAsync(new ReserveRateCommand(bucketKey, jobId, intervalMilliseconds, burst, graceSeconds), ct);

    /// <summary>
    /// CAS on the hold token, which is unchanged so the same token still releases; not extended when the lock
    /// had been stolen or reacquired. The engine's lock-lease heartbeat calls it to keep a long-running
    /// handler's locks alive by moving <c>locks.expires_at_utc</c>.
    /// </summary>
    public Task<ExtendLockResponse> ExtendLockAsync(LockToken token, TimeSpan ttl, CancellationToken ct) =>
        extendLock.HandleAsync(new ExtendLockCommand(token, ttl), ct);

    /// <summary>CAS on the hold token; not released when it had already been stolen/reacquired.</summary>
    public Task<ReleaseLockResponse> ReleaseLockAsync(LockToken token, CancellationToken ct) =>
        releaseLock.HandleAsync(new ReleaseLockCommand(token), ct);
}
