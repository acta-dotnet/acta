namespace Acta.Runtime.Services.Locks;

/// <summary>
/// Swappable mutual-exclusion lock store: the single seam behind <c>JobContext.RunWithLock</c> and
/// the <c>exclusive_key</c> execution mutex (<c>{ns_id}.excl.{key}</c> rows taken by the runner
/// after claim, before the handler; a loser re-arms Ready after the fixed bounce delay).
/// Naming: the <c>Locks</c> slice is the public locking facade; the physical rows live in the
/// <c>leases</c> table (kind <c>lock</c>), written by the provider lock store.
/// The provider leases-backed implementation is the default, Redis-free store; a
/// Redis-backed store can be substituted with no caller change. No-wait: a single attempt, so the
/// caller owns any retry/backoff.
/// </summary>
internal interface ILockStore
{
    /// <summary>
    /// Single no-wait acquire of <paramref name="key"/> on behalf of <paramref name="ownerJobId"/>.
    /// Returns a <see cref="LockToken"/> on success (carry it to <see cref="ReleaseAsync"/>), or
    /// <c>null</c> when the lock is currently held.
    /// </summary>
    Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct);

    /// <summary>
    /// Extends the lease of the lock held under <paramref name="token"/> by <paramref name="ttl"/>
    /// (CAS on the token's version; the version is unchanged so the same token still releases). Returns
    /// <c>true</c> when the caller still held it; <c>false</c> when it had been stolen/reacquired.
    /// </summary>
    /// <remarks>
    /// Called by the worker heartbeat to keep a long-running handler's concurrency lock alive. Routes
    /// through the same swappable seam as acquire/release, so a Redis store extends via key expiry while
    /// the provider store bumps <c>leases.expires_at_utc</c>.
    /// </remarks>
    Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct);

    /// <summary>
    /// Releases the lock held under <paramref name="token"/> (CAS on the token's version). Returns
    /// <c>true</c> when the caller still held it; <c>false</c> when it had already been
    /// stolen/reacquired.
    /// </summary>
    Task<bool> ReleaseAsync(LockToken token, CancellationToken ct);
}

/// <summary>
/// Opaque handle to a held lock: the composed key plus the per-hold CAS version. Release-only use.
/// </summary>
internal readonly record struct LockToken(string Key, int Version);
