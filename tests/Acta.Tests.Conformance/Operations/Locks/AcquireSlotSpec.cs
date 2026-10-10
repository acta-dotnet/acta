using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Locks;

/// <summary>
/// Conformance for <c>acquire_slot</c>, the counted-key admission behind a concurrency limit. How the
/// runner composes slot keys and shares them across definitions is specified in <c>ConcurrencyLimitSpec</c>.
/// </summary>
[ConformanceSpec(
    "acquire-slot.lowest-free",
    "A slot acquire takes the lowest free slot of a counted key, or none",
    Area = "Locks",
    Contract = "AcquireSlot takes the lowest slot under the limit that is unheld or expired, under the caller's owner and a fresh token, or returns null when none is."
)]
public sealed class AcquireSlotSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly TimeSpan HeldTtl = TimeSpan.FromMinutes(5);

    // A lease that ran out a second ago; a zero-length one can round a fraction into the future.
    private static readonly TimeSpan ExpiredTtl = TimeSpan.FromSeconds(-1);

    [Fact(DisplayName = "The lowest free slot is taken first, and its row records the owner, the minted token, and a lease the TTL long")]
    public async Task Lowest_free_slot_is_taken_with_owner_token_and_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<LocksOperations>();
        var prefix = TestKey("slots");
        var dbNow = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

        var first = Assert.NotNull((await locks.AcquireSlotAsync(prefix, limit: 3, TimeSpan.FromSeconds(60), ownerJobId: -1, ct)).Token);
        var second = Assert.NotNull((await locks.AcquireSlotAsync(prefix, limit: 3, TimeSpan.FromSeconds(60), ownerJobId: -2, ct)).Token);

        Assert.Equal($"{prefix}.0", first.Key);
        Assert.Equal($"{prefix}.1", second.Key);
        var row = await ReadOneAsync(first.Key, ct);
        Assert.Equal(-1L, row.JobId);
        Assert.Equal(first.HoldToken, row.HoldToken);
        Assert.InRange(row.ExpiresAtUtc, dbNow.AddSeconds(59), dbNow.AddSeconds(70));
        Assert.NotEqual(first.HoldToken, second.HoldToken);
    }

    [Fact(DisplayName = "With every slot under the limit held the acquire returns null and writes no slot past the limit")]
    public async Task Full_key_returns_null_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<LocksOperations>();
        var prefix = TestKey("full");
        var held = new List<LockToken>();
        for (var i = 0; i < 2; i++)
        {
            held.Add(Assert.NotNull((await locks.AcquireSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: -1 - i, ct)).Token));
        }

        Assert.Null((await locks.AcquireSlotAsync(prefix, limit: 2, HeldTtl, ownerJobId: -9, ct)).Token);

        foreach (var token in held)
        {
            Assert.Equal(token.HoldToken, (await ReadAsync(token.Key, ct))?.HoldToken);
        }
        Assert.Null(await ReadAsync($"{prefix}.2", ct));
    }

    [Fact(DisplayName = "An expired slot is stolen in place before any higher free slot, and its old token no longer releases")]
    public async Task Expired_slot_is_stolen_before_a_higher_free_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<LocksOperations>();
        var prefix = TestKey("steal");
        Assert.Equal($"{prefix}.0", (await locks.AcquireSlotAsync(prefix, limit: 3, HeldTtl, ownerJobId: -1, ct)).Token?.Key);
        var lapsed = Assert.NotNull((await locks.AcquireSlotAsync(prefix, limit: 3, ExpiredTtl, ownerJobId: -2, ct)).Token);
        Assert.Equal($"{prefix}.1", lapsed.Key);

        var stolen = Assert.NotNull((await locks.AcquireSlotAsync(prefix, limit: 3, HeldTtl, ownerJobId: -3, ct)).Token);

        Assert.Equal($"{prefix}.1", stolen.Key);
        var row = await ReadOneAsync(stolen.Key, ct);
        Assert.Equal(-3L, row.JobId);
        Assert.Equal(stolen.HoldToken, row.HoldToken);
        Assert.Null(await ReadAsync($"{prefix}.2", ct));
        Assert.False((await locks.ReleaseLockAsync(lapsed, ct)).Released);
    }

    [Fact(DisplayName = "Simultaneous acquires of a key's one free slot leave exactly one holder")]
    public async Task Concurrent_acquires_have_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = Services.GetRequiredService<LocksOperations>();
        var prefix = TestKey("race");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable
            .Range(1, 8)
            .Select(async i =>
            {
                await start.Task;
                return (await locks.AcquireSlotAsync(prefix, limit: 1, HeldTtl, ownerJobId: -i, ct)).Token;
            })
            .ToArray();
        start.SetResult();

        var winner = Assert.Single(await Task.WhenAll(attempts), t => t.HasValue)!.Value;

        Assert.Equal($"{prefix}.0", winner.Key);
        Assert.Equal(winner.HoldToken, (await ReadAsync(winner.Key, ct))?.HoldToken);
    }

    [Theory(DisplayName = "A limit below one throws before any slot is written")]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Limit_below_one_throws(int limit)
    {
        var ct = TestContext.Current.CancellationToken;
        var prefix = TestKey("no-slots");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await Services
                .GetRequiredService<LocksOperations>()
                .AcquireSlotAsync(prefix, limit, TimeSpan.FromSeconds(60), ownerJobId: -1, ct)
        );
        Assert.Null(await ReadAsync($"{prefix}.0", ct));
    }

    private Task<Acta.LockEntity?> ReadAsync(string key, CancellationToken ct) =>
        Db.From<Acta.LockEntity>().Where(l => l.LockKey == key).SingleOrDefaultAsync(ct);

    private async Task<Acta.LockEntity> ReadOneAsync(string key, CancellationToken ct) =>
        Assert.Single(await Db.From<Acta.LockEntity>().Where(l => l.LockKey == key).ToListAsync(ct));
}
