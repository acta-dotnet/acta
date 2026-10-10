namespace Acta;

/// <summary><see cref="ILocksOperations"/>, one line per operation.</summary>
internal sealed class LocksOperations(
    AcquireLockHandler acquireLock,
    AcquireSlotHandler acquireSlot,
    ReserveRateHandler reserveRate,
    ReleaseLockHandler releaseLock
) : ILocksOperations
{
    public Task<AcquireLockResponse> AcquireLockAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
        acquireLock.HandleAsync(new AcquireLockCommand(key, ttl, ownerJobId), ct);

    public Task<AcquireSlotResponse> AcquireSlotAsync(string keyPrefix, int limit, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
        acquireSlot.HandleAsync(new AcquireSlotCommand(keyPrefix, limit, ttl, ownerJobId), ct);

    public Task<ReserveRateResponse> ReserveRateAsync(
        string bucketKey,
        long jobId,
        int intervalMilliseconds,
        int burst,
        int graceSeconds,
        CancellationToken ct
    ) => reserveRate.HandleAsync(new ReserveRateCommand(bucketKey, jobId, intervalMilliseconds, burst, graceSeconds), ct);

    public Task<ReleaseLockResponse> ReleaseLockAsync(LockToken token, CancellationToken ct) =>
        releaseLock.HandleAsync(new ReleaseLockCommand(token), ct);
}
