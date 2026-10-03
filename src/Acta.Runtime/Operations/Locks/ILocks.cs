namespace Acta.Runtime;

/// <summary>
/// The locks the engine takes, behind <c>JobContext.RunWithLock</c> and the concurrency-slot
/// admission (<c>{ns_id}.sem.{key}.{slot}</c> rows taken by the runner after claim; a loser re-arms
/// Ready after the fixed bounce delay). <see cref="Locks"/> runs each on the <c>locks</c> table; the
/// interface is the seam engine unit tests fake. No-wait: a single attempt, so the caller owns any
/// retry/backoff.
/// </summary>
internal interface ILocks
{
    /// <summary>Null when the lock is currently held; carry the token to ReleaseLockAsync.</summary>
    Task<LockToken?> AcquireLockAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct);

    /// <summary>
    /// Takes the lowest-numbered free slot of a counted key in one round trip, whatever the limit:
    /// the slots are the rows <c>{keyPrefix}.0</c> .. <c>{keyPrefix}.{limit-1}</c>, and a slot is free
    /// when it has no row or its hold expired (steal on expiry, as AcquireLockAsync does). Null when
    /// every slot is held, and also when a racer took the chosen slot first: the routine makes one
    /// attempt and never retries, so the caller settles the loss rather than waiting.
    /// The returned token carries the slot's own key, so extend and release need no slot arithmetic.
    /// </summary>
    Task<LockToken?> AcquireSlotAsync(string keyPrefix, int limit, TimeSpan ttl, long ownerJobId, CancellationToken ct);

    /// <summary>
    /// Spends one turn from the GCRA meter at <paramref name="bucketKey"/>: each request moves its arrival
    /// time one interval on, and an idle meter admits exactly <paramref name="burst"/> back to back.
    /// Admitted means run now; otherwise the turn is booked at the returned instant, measured on the
    /// store's clock so a caller sleeps or re-arms to it without racing the meter. The bucket lapses once
    /// an idle meter equals a missing one, and a reservation lapses <paramref name="graceSeconds"/> past
    /// its turn; a turn stale by more than a second (or one interval) is re-metered rather than released.
    /// </summary>
    Task<RateReservation> ReserveRateAsync(
        string bucketKey,
        long jobId,
        int intervalMilliseconds,
        int burst,
        int graceSeconds,
        CancellationToken ct
    );

    /// <summary>
    /// CAS on the hold token, which is unchanged so the same token still releases; false when the lock
    /// had been stolen or reacquired. <see cref="LockLeaseHeartbeat"/> calls it to keep a long-running
    /// handler's locks alive by moving <c>locks.expires_at_utc</c>.
    /// </summary>
    Task<bool> ExtendLockAsync(LockToken token, TimeSpan ttl, CancellationToken ct);

    /// <summary>CAS on the hold token; false when it had already been stolen/reacquired.</summary>
    Task<bool> ReleaseLockAsync(LockToken token, CancellationToken ct);
}
