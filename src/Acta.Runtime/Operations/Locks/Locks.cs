namespace Acta.Runtime;

/// <summary><see cref="ILocks"/>, one line per operation.</summary>
internal sealed class Locks(
    AcquireLockHandler acquireLock,
    AcquireSlotHandler acquireSlot,
    ReserveRateHandler reserveRate,
    ExtendLockHandler extendLock,
    ReleaseLockHandler releaseLock
) : ILocks
{
    public Task<LockToken?> AcquireLockAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
        acquireLock.HandleAsync(new AcquireLockCommand(key, ttl, ownerJobId), ct);

    public Task<LockToken?> AcquireSlotAsync(string keyPrefix, int limit, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
        acquireSlot.HandleAsync(new AcquireSlotCommand(keyPrefix, limit, ttl, ownerJobId), ct);

    public Task<RateReservation> ReserveRateAsync(
        string bucketKey,
        long jobId,
        int intervalMilliseconds,
        int burst,
        int graceSeconds,
        CancellationToken ct
    ) => reserveRate.HandleAsync(new ReserveRateCommand(bucketKey, jobId, intervalMilliseconds, burst, graceSeconds), ct);

    public Task<bool> ExtendLockAsync(LockToken token, TimeSpan ttl, CancellationToken ct) =>
        extendLock.HandleAsync(new ExtendLockCommand(token, ttl), ct);

    public Task<bool> ReleaseLockAsync(LockToken token, CancellationToken ct) => releaseLock.HandleAsync(new ReleaseLockCommand(token), ct);
}
