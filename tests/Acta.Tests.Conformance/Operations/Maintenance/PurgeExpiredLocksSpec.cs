using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for <c>sys.retention</c>'s lock delete (<c>PurgeExpiredLocks</c>): lock rows past their expiry go and a
/// live one stays, within the batch bounds every delete keeps. The delete is global, so each fact reads only its own
/// <c>Unique</c> keys.
/// </summary>
[ConformanceSpec(
    "purge-expired-locks.sweeps",
    "Purge deletes expired lock rows within batches",
    Area = "Retention",
    Contract = "Purge deletes lock rows past their expiry, keeps live ones, and deletes no more per call than its batch bounds allow."
)]
[Deploys("add-numbers")]
public sealed class PurgeExpiredLocksSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeExpiredLocksSpec>>
{
    // A window that makes the dated deletes a no-op for the rows produced in these tests.
    private const int NoPurgeDays = 100_000;

    [Fact(DisplayName = "An expired lock row is reaped and a live lock is kept")]
    public async Task Expired_lock_row_is_reaped_and_a_live_lock_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Per-test keys (Unique needle): the live row outlives the test by its TTL in the shared
        // schema, so a constant key would block re-acquisition on the next run; uniqueness also keeps
        // concurrent specs from stealing them. One lock held with a long TTL, one whose negative TTL
        // puts its expiry in the past, making it immediately reapable.
        var liveKey = Unique("reap-spec.live");
        var deadKey = Unique("reap-spec.dead");
        var liveToken = (
            await Services.GetRequiredService<LocksOperations>().AcquireLockAsync(liveKey, TimeSpan.FromSeconds(3600), ownerJobId: -1, ct)
        ).Token;
        Assert.NotNull(liveToken);
        Assert.NotNull(
            (
                await Services.GetRequiredService<LocksOperations>().AcquireLockAsync(deadKey, TimeSpan.FromSeconds(-1), ownerJobId: -1, ct)
            ).Token
        );

        try
        {
            // The reap is global (locks carry no namespace), so a concurrent spec's sweep may stage the dead
            // row first; this sweep then skips it under READPAST and the row is gone once that one commits.
            await RetentionTestOps.PurgeUntilAsync(
                Services,
                ns,
                NoPurgeDays,
                1000,
                50,
                async () => await Db.From<Acta.LockEntity>().Where(l => l.LockKey == deadKey).SingleOrDefaultAsync(ct) is null,
                ct
            );

            Assert.Null(await Db.From<Acta.LockEntity>().Where(l => l.LockKey == deadKey).SingleOrDefaultAsync(ct));
            Assert.NotNull(await Db.From<Acta.LockEntity>().Where(l => l.LockKey == liveKey).SingleOrDefaultAsync(ct));
        }
        finally
        {
            // Drop the held row so the shared schema carries no hour-long lease out of this test.
            await Services.GetRequiredService<LocksOperations>().ReleaseLockAsync(liveToken!.Value, ct);
        }
    }

    [Fact(DisplayName = "The lock sweep is bounded by batch size and iterations like every other delete")]
    public async Task Lock_sweep_is_bounded_by_batch_size_and_iterations()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var locks = Services.GetRequiredService<LocksOperations>();

        // Three immediately-reapable rows (negative TTL) plus one live row. The reap is global, so a
        // concurrent spec's full purge may sweep some dead rows first; the call's OWN count is still
        // deterministically capped by batch x iterations, which is the property under test.
        List<string> deadKeys = [Unique("lock-batch.d1"), Unique("lock-batch.d2"), Unique("lock-batch.d3")];
        foreach (var key in deadKeys)
        {
            Assert.NotNull((await locks.AcquireLockAsync(key, TimeSpan.FromSeconds(-1), ownerJobId: -1, ct)).Token);
        }
        var liveKey = Unique("lock-batch.live");
        var liveToken = (await locks.AcquireLockAsync(liveKey, TimeSpan.FromSeconds(3600), ownerJobId: -1, ct)).Token;
        Assert.NotNull(liveToken);

        try
        {
            var bounded = await RetentionTestOps.PurgeAsync(Services, ns, NoPurgeDays, batchSize: 1, maxIterations: 1, ct);
            Assert.True(bounded.Locks <= 1, $"lock sweep deleted {bounded.Locks} rows under a 1x1 budget");

            // Full-budget runs clear this test's remaining dead rows.
            Task<IReadOnlyList<Acta.LockEntity>> DeadAsync() =>
                Db.From<Acta.LockEntity>().Where(l => deadKeys.Contains(l.LockKey)).ToListAsync(ct);
            await RetentionTestOps.PurgeUntilAsync(Services, ns, NoPurgeDays, 1000, 50, async () => (await DeadAsync()).Count == 0, ct);
            Assert.Empty(await DeadAsync());
            Assert.NotNull(await Db.From<Acta.LockEntity>().Where(l => l.LockKey == liveKey).SingleOrDefaultAsync(ct));
        }
        finally
        {
            await locks.ReleaseLockAsync(liveToken!.Value, ct);
        }
    }
}
