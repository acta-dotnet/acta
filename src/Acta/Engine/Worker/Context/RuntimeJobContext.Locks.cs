#nullable enable

using Microsoft.Extensions.Logging;

namespace Acta;

internal sealed partial class RuntimeJobContext
{
    private protected override async Task<Guid?> AcquireLockCoreAsync(string key, LockScope scope, CancellationToken ct)
    {
        var token = (await _locks.AcquireLockAsync(ComposeLockKey(key, scope), TimeSpan.FromSeconds(_leaseTtlSeconds), JobId, ct)).Token;
        if (token is { } held)
        {
            // Register so the heartbeat renews this handler-acquired lock for as long as the
            // critical section runs; without it a long action would outlive the lease TTL and the lock
            // could be stolen mid-section.
            _inFlightJob?.TrackLock(held);
        }
        return token?.HoldToken;
    }

    private protected override Task ReleaseLockCoreAsync(string key, LockScope scope, Guid holdToken, CancellationToken ct)
    {
        var token = new LockToken(ComposeLockKey(key, scope), holdToken);
        // Untrack before the store release so a heartbeat tick racing this release sees Holds()==false
        // and does not mistake the normal release for a steal.
        _inFlightJob?.UntrackLock(token);
        return _locks.ReleaseLockAsync(token, ct);
    }

    private protected override void OnLockReleaseFailure(string key, LockScope scope, Exception exception) =>
        RecordLockReleaseFailure(scope == LockScope.Global ? "handler_global" : "handler_namespace", ComposeLockKey(key, scope), exception);

    private LockToken? _concurrencySlotToken;

    /// <summary>
    /// Concurrency-slot admission, taken by the runner after the start CAS and before the handler.
    /// The key owns slots 0..limit-1 under the prefix {ns_id}.sem.{key}, disjoint from RunWithLock's
    /// {ns_id}.lock.{key} / global.lock.{key}; limit 1 is slot 0 alone, the mutex. The key is
    /// canonicalized so one group across case never depends on the stored value alone, without the
    /// public system-prefix rule: an enqueue key was already rejected at enqueue, and a key derived
    /// from a definition name may legitimately be a system job's (sys.recovery with a limit).
    /// </summary>
    internal async Task<bool> TryAcquireConcurrencySlotAsync(string concurrencyKey, int limit, CancellationToken ct)
    {
        var prefix = $"{_namespaceId}.sem.{IdentifierSyntax.NormalizeKeyLookup(concurrencyKey, nameof(concurrencyKey))}";
        var token = (await _locks.AcquireSlotAsync(prefix, limit, TimeSpan.FromSeconds(_leaseTtlSeconds), JobId, ct)).Token;
        if (token is { } held)
        {
            _inFlightJob?.TrackLock(held);
            _concurrencySlotToken = held;
        }
        return token is not null;
    }

    /// <summary>
    /// Rate admission, taken by the runner after the concurrency slot and before the handler. The meter is the bucket
    /// row {ns_id}.rate.{key}, disjoint from the lock and slot key spaces, and holds nothing: it is moved, never
    /// acquired, so there is no release. The key is canonicalized like the slot key, system names allowed for the same
    /// reason. The grace a booked turn gets before the sweep may take it is a constant, not the lease TTL: the turn is
    /// decoded as expiry minus grace, so a value moving with worker settings would shift every booked turn.
    /// </summary>
    internal Task<ReserveRateResponse> ReserveRateAsync(string rateKey, RateLimitSpec rate, CancellationToken ct)
    {
        var bucket = $"{_namespaceId}.rate.{IdentifierSyntax.NormalizeKeyLookup(rateKey, nameof(rateKey))}";
        return _locks.ReserveRateAsync(bucket, JobId, rate.IntervalMilliseconds, rate.Burst, RateReservationGraceSeconds, ct);
    }

    /// <summary>
    /// How long a booked rate turn outlives its instant before the lock expiry sweep may take it:
    /// fifteen minutes, longer than any default lease, and fixed so it never moves with settings.
    /// </summary>
    internal const int RateReservationGraceSeconds = 900;

    internal async Task ReleaseConcurrencySlotAsync(CancellationToken ct)
    {
        if (_concurrencySlotToken is not { } token)
        {
            return;
        }
        _concurrencySlotToken = null;
        _inFlightJob?.UntrackLock(token);
        try
        {
            await _locks.ReleaseLockAsync(token, ct);
        }
        catch (Exception ex)
        {
            RecordLockReleaseFailure("concurrency_slot", token.Key, ex);
        }
    }

    private void RecordLockReleaseFailure(string lockKind, string key, Exception exception)
    {
        _log.LogWarning(
            exception,
            "Failed to release the ({Detail}) held for job {JobId}; continuing because the lock TTL will clean it up.",
            $"{lockKind} lock '{key}'",
            JobId
        );
        _metrics?.RecordLockReleaseFailure(JobNamespace, JobName, lockKind, exception.GetType().Name);
    }

    /// <summary>
    /// RunWithLock key space: namespace-scoped {ns_id}.lock.{key}, or global.lock.{key}
    /// cross-namespace. The `global` sentinel cannot equal a numeric namespace id, so a global lock
    /// never collides with a namespace-scoped one.
    /// </summary>
    private string ComposeLockKey(string key, LockScope scope)
    {
        key = IdentifierSyntax.NormalizeKey(key, nameof(key));
        return scope == LockScope.Global ? $"global.lock.{key}" : $"{_namespaceId}.lock.{key}";
    }
}
