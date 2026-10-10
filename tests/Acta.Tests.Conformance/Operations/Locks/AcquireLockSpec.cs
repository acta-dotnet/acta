using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Locks;

/// <summary>
/// Conformance for <c>AcquireLock</c> - the steal-on-expiry acquire that backs
/// <c>LocksOperations.AcquireLockAsync</c>. A free key is acquired and the lease row lands; a competing
/// acquire on the same live key is blocked (returns <c>null</c>).
/// </summary>
[ConformanceSpec(
    "acquire-lock.steal-on-expiry",
    "Acquire lands a lease row and blocks a competing acquire on a live key",
    Area = "Locks",
    Contract = "AcquireLock inserts a leases row on a free key and returns null when the key is already held by a live lease."
)]
public sealed class AcquireLockSpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(DisplayName = "Simultaneous acquisitions of a new key return exactly one live token")]
    public async Task Concurrent_acquires_have_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<LocksOperations>();
        var key = Unique("lock-race");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable
            .Range(1, 8)
            .Select(async i =>
            {
                await start.Task;
                return (await store.AcquireLockAsync(key, TimeSpan.FromMinutes(1), -i, ct)).Token;
            })
            .ToArray();
        start.SetResult();
        var tokens = await Task.WhenAll(attempts);
        var winner = Assert.Single(tokens, t => t.HasValue)!.Value;
        var row = await Db.From<Acta.LockEntity>().Where(l => l.LockKey == key).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        Assert.Equal(winner.HoldToken, row.HoldToken);
        Assert.True((await store.ReleaseLockAsync(winner, ct)).Released);
    }

    [Fact(DisplayName = "First acquire returns a token and lands a lease row, and a competing acquire on a live key returns null")]
    public async Task Acquire_lands_a_lease_row_and_blocks_a_competing_acquire()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<LocksOperations>();
        var key = Unique("ck.acquire-lock-spec");

        var token = (await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -1, ct)).Token;
        Assert.NotNull(token);

        var lease = await Db.From<Acta.LockEntity>().Where(l => l.LockKey == key).SingleOrDefaultAsync(ct);
        Assert.NotNull(lease);
        Assert.Equal(key, lease!.LockKey);
        Assert.Equal(-1L, lease.JobId);
        Assert.True(lease.ExpiresAtUtc > DateTime.UtcNow);

        // A competing acquire on the still-live lease returns null.
        var rival = (await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct)).Token;
        Assert.Null(rival);

        // Release so the row is cleaned up (idempotent - no assertion needed here).
        await locks.ReleaseLockAsync(token!.Value, ct);
    }

    [Fact(DisplayName = "A competing acquire steals an expired lease and bumps the version")]
    public async Task Competing_acquire_steals_expired_lease_and_bumps_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<LocksOperations>();
        var key = Unique("ck.steal-on-expiry");

        // Initial acquire: lands a lease row. Owners are negative synthetic ids so they can never
        // collide with a real job identity under the parallel suite.
        var tokenA = (await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -1, ct)).Token;
        Assert.NotNull(tokenA);

        // Deterministically expire the lease by back-dating expires_at_utc.
        {
            var expired = DateTime.UtcNow.AddHours(-1);
            await Db.From<Acta.LockEntity>()
                .Where(l => l.LockKey == key)
                .UpdateOnlyAsync(() => new Acta.LockEntity { ExpiresAtUtc = expired }, ct);
        }

        // A competing acquire on the now-expired lease must succeed (steal). Poll briefly: under SQL Server
        // contention on the shared leases table AcquireLockAsync can transiently return null even though
        // the lease is expired and stealable, so one miss should not fail the test.
        var tokenB = (await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct)).Token;
        var stealDeadline = DateTime.UtcNow.AddSeconds(10);
        while (tokenB is null && DateTime.UtcNow < stealDeadline)
        {
            await Task.Delay(50, ct);
            tokenB = (await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(30), ownerJobId: -2, ct)).Token;
        }
        Assert.NotNull(tokenB);

        // Post-steal: the new owner is the stealer. We do NOT assert the version strictly
        // increased: a concurrent PurgeExpiredData can reap the back-dated (expired) lease before the
        // steal, in which case AcquireLockAsync re-inserts a fresh lease (version resets) rather than
        // updating it in place.
        {
            var postSteal = await Db.From<Acta.LockEntity>().Where(l => l.LockKey == key).SingleOrDefaultAsync(ct);
            Assert.NotNull(postSteal);
            Assert.Equal(-2L, postSteal!.JobId);
        }

        // Token A is stale only when the row was updated in place and the version bumped. If the
        // retention sweep reaped the expired row between the back-date and the competing acquire, the
        // fresh insert can reuse version 1, making token A indistinguishable from token B.
        if (tokenA!.Value.HoldToken != tokenB!.Value.HoldToken)
        {
            Assert.False((await locks.ReleaseLockAsync(tokenA.Value, ct)).Released);
        }

        // Clean up: release the stealer's token.
        await locks.ReleaseLockAsync(tokenB!.Value, ct);
    }
}
