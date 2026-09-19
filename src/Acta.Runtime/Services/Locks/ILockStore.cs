namespace Acta.Runtime.Services.Locks;

/// <summary>
/// Swappable mutual-exclusion seam behind <c>JobContext.RunWithLock</c> and the concurrency-slot
/// admission (<c>{ns_id}.sem.{key}.{slot}</c> rows taken by the runner after claim; a loser re-arms
/// Ready after the fixed bounce delay). The provider locks-backed store is the default, Redis-free
/// implementation; a Redis-backed store substitutes with no caller change. No-wait: a single
/// attempt, so the caller owns any retry/backoff.
/// </summary>
internal interface ILockStore
{
    /// <summary>Null when the lock is currently held; carry the token to ReleaseAsync.</summary>
    Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct);

    /// <summary>
    /// Takes the lowest-numbered free slot of a counted key in one round trip, whatever the limit:
    /// the slots are the rows <c>{keyPrefix}.0</c> .. <c>{keyPrefix}.{limit-1}</c>, and a slot is free
    /// when it has no row or its hold expired (steal on expiry, as TryAcquireAsync does). Null when
    /// every slot is held, and also when a racer took the chosen slot first: the routine makes one
    /// attempt and never retries, so the caller settles the loss rather than waiting.
    /// The returned token carries the slot's own key, so extend and release need no slot arithmetic.
    /// </summary>
    Task<LockToken?> TryAcquireSlotAsync(string keyPrefix, int limit, TimeSpan ttl, long ownerJobId, CancellationToken ct);

    /// <summary>
    /// Spends one turn from a rate meter, reserving the next one when the meter is ahead of now. The
    /// meter is a GCRA bucket row at <paramref name="bucketKey"/> holding its theoretical arrival
    /// time; every request moves it forward by <paramref name="intervalMilliseconds"/>, and an idle
    /// bucket is treated as far enough behind to hand out exactly <paramref name="burst"/> admissions
    /// back to back, which is what makes the burst a burst. Admitted means the returned instant has
    /// passed and the caller may run now. Not admitted means the caller must re-arm at the returned
    /// instant, where a reservation row is already waiting for this job: the turn is booked, so the
    /// caller bounces once rather than racing the meter again, and a queue drains at the rate with one
    /// re-arm each. Both rows carry an instant rather than a lease, and the bucket's is offset so that
    /// it expires exactly when an idle meter stops differing from a missing one.
    /// </summary>
    Task<RateReservation> ReserveRateAsync(string bucketKey, long jobId, int intervalMilliseconds, int burst, CancellationToken ct);

    /// <summary>
    /// CAS on the hold token, which is unchanged so the same token still releases; false when the
    /// lock had been stolen/reacquired. Called by the worker heartbeat to keep a long-running
    /// handler's concurrency lock alive - a Redis store extends via key expiry, the provider
    /// store bumps <c>locks.expires_at_utc</c>.
    /// </summary>
    Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct);

    /// <summary>CAS on the hold token; false when it had already been stolen/reacquired.</summary>
    Task<bool> ReleaseAsync(LockToken token, CancellationToken ct);
}

/// <summary>
/// Composed key plus the per-hold CAS token minted at acquire. A token, not a counter:
/// delete-and-reacquire can never re-mint it, so a stale holder cannot free or extend a
/// successor's lock.
/// </summary>
internal readonly record struct LockToken(string Key, Guid HoldToken);

/// <summary>
/// One rate-meter answer. <paramref name="ResumeAtUtc"/> is the instant this job's turn comes and is
/// meaningful only when <paramref name="Admitted"/> is false; an admitted request reads back the
/// store's clock, because the turn is now.
/// </summary>
internal readonly record struct RateReservation(bool Admitted, DateTime ResumeAtUtc);
