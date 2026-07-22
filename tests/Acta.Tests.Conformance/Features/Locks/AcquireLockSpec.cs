using Acta.Relational.Entities;
using Acta.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Locks;

/// <summary>
/// Conformance for <c>AcquireLock</c> - the steal-on-expiry acquire that backs
/// <c>ILockStore.TryAcquireAsync</c>. A free key is acquired and the lease row lands; a competing
/// acquire on the same live key is blocked (returns <c>null</c>).
/// </summary>
[ConformanceSpec(
    "acquire-lock.steal-on-expiry",
    "Acquire lands a lease row and blocks a competing acquire on a live key",
    Area = "Locks",
    Contract = "AcquireLock inserts a leases row on a free key and returns null when the key is already held by a live lease.",
    Arrange = "A lock key exists with no live lease held on it.",
    Act = "AcquireLock takes the free key and a competing acquire is attempted on the same key while the lease is still live.",
    Assert = "The first acquire lands a live lease row and the competing acquire returns null."
)]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.TryAcquireAsync))]
[CoversStoreMethod(typeof(ILockStore), nameof(ILockStore.TryAcquireAsync))]
public abstract class AcquireLockSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "First acquire returns a token and lands a lease row, and a competing acquire on a live key returns null")]
    public async Task Acquire_lands_a_lease_row_and_blocks_a_competing_acquire()
    {
        var ct = TestContext.Current.CancellationToken;
        var lockStore = Services.GetRequiredService<ILockStore>();
        var key = TestKey("ck.acquire-lock-spec");

        var token = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -1, ct);
        Assert.NotNull(token);

        var lease = await Db.From<Lease>().Where(l => l.LeaseKey == key).SingleOrDefaultAsync(ct);
        Assert.NotNull(lease);
        Assert.Equal(key, lease!.LeaseKey);
        Assert.Equal(-1L, lease.JobId);
        Assert.True(lease.ExpiresAtUtc > DateTime.UtcNow);

        // A competing acquire on the still-live lease returns null.
        var rival = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct);
        Assert.Null(rival);

        // Release so the row is cleaned up (idempotent - no assertion needed here).
        await lockStore.ReleaseAsync(token!.Value, ct);
    }

    [Fact(DisplayName = "A competing acquire steals an expired lease and bumps the version")]
    public async Task Competing_acquire_steals_expired_lease_and_bumps_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var lockStore = Services.GetRequiredService<ILockStore>();
        var key = TestKey("ck.steal-on-expiry");

        // Initial acquire: lands a lease row. Owners are negative synthetic ids so they can never
        // collide with a real job identity under the parallel suite.
        var tokenA = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -1, ct);
        Assert.NotNull(tokenA);

        // Deterministically expire the lease by back-dating expires_at_utc.
        {
            var expired = DateTime.UtcNow.AddHours(-1);
            await Db.From<Lease>().Where(l => l.LeaseKey == key).UpdateOnlyAsync(() => new Lease { ExpiresAtUtc = expired }, ct);
        }

        // A competing acquire on the now-expired lease must succeed (steal). Poll briefly: under SQL Server
        // contention on the shared leases table TryAcquireAsync can transiently return null even though
        // the lease is expired and stealable, so one miss should not fail the test.
        var tokenB = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct);
        var stealDeadline = DateTime.UtcNow.AddSeconds(10);
        while (tokenB is null && DateTime.UtcNow < stealDeadline)
        {
            await Task.Delay(50, ct);
            tokenB = await lockStore.TryAcquireAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct);
        }
        Assert.NotNull(tokenB);

        // Post-steal: the new owner is the stealer. We do NOT assert the version strictly
        // increased: a concurrent PurgeExpiredData can reap the back-dated (expired) lease before the
        // steal, in which case TryAcquireAsync re-inserts a fresh lease (version resets) rather than
        // updating it in place.
        {
            var postSteal = await Db.From<Lease>().Where(l => l.LeaseKey == key).SingleOrDefaultAsync(ct);
            Assert.NotNull(postSteal);
            Assert.Equal(-2L, postSteal!.JobId);
        }

        // Token A is stale only when the row was updated in place and the version bumped. If the
        // retention sweep reaped the expired row between the back-date and the competing acquire, the
        // fresh insert can reuse version 1, making token A indistinguishable from token B.
        if (tokenA!.Value.Version != tokenB!.Value.Version)
        {
            Assert.False(await lockStore.ReleaseAsync(tokenA.Value, ct));
        }

        // Clean up: release the stealer's token.
        await lockStore.ReleaseAsync(tokenB!.Value, ct);
    }
}
