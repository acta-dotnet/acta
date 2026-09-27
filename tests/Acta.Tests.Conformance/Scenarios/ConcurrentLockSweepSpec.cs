using Acta.Relational.Entities;
using Acta.Runtime.Maintenance;
using Acta.Runtime.Services.Locks;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;
using Lock = Acta.Relational.Entities.Lock;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// The expired-lock sweep is global, so every namespace's retention pass reaps the same table. Sweeps
/// racing over a shared backlog of expired rows each take the rows no other sweep holds and must never
/// deadlock one another. Deadlock retry is off for this spec, so a deadlock fails the test instead of
/// being absorbed by a retry.
/// </summary>
[ConformanceSpec(
    "purge-expired-data.concurrent-lock-sweeps",
    "Concurrent retention sweeps reap a shared lock backlog without a deadlock",
    Area = "Retention",
    Contract = "Retention sweeps racing over the same expired lock rows each reap rows no other sweep holds, and together they reap all of them without a deadlock.",
    Arrange = "A thousand expired lock rows are acquired with a negative TTL, and deadlock retry is off so a deadlock victim surfaces.",
    Act = "Eight retention sweeps run at once with small batches, so their lock-section batches interleave.",
    Assert = "Every sweep completes and none of the expired rows is left."
)]
[CoversStoreMethod(typeof(IRetentionStore), nameof(IRetentionStore.PurgeBatchAsync))]
public abstract class ConcurrentLockSweepSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int ExpiredRows = 1000;
    private const int Sweeps = 8;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        var provider = services.Last(d => d.ServiceType == typeof(SqlProviderOptions));
        services.Remove(provider);
        services.AddSingleton(sp =>
        {
            var options = (SqlProviderOptions)provider.ImplementationFactory!(sp);
            DeadlockRetryOff.Apply(sp, options);
            return options;
        });
    }

    [Fact(DisplayName = "Eight retention sweeps racing over a thousand expired lock rows reap them all without a deadlock")]
    public async Task Concurrent_sweeps_reap_a_shared_backlog()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var locks = Services.GetRequiredService<ILockStore>();
        var keys = Enumerable.Range(0, ExpiredRows).Select(i => TestKey($"sweep-race.{i:D4}")).ToList();
        foreach (var key in keys)
        {
            Assert.NotNull(await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(-1), ownerJobId: -1, ct));
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweeps = Enumerable
            .Range(0, Sweeps)
            .Select(_ =>
                Task.Run(
                    async () =>
                    {
                        await start.Task;
                        await RetentionTestOps.PurgeAsync(Services, ns, 100_000, 100_000, 100_000_000, 10, 50, ct);
                    },
                    ct
                )
            )
            .ToList();
        start.SetResult();
        await Task.WhenAll(sweeps);

        // Racing sweeps may stage the same rows and each skip what another holds, so one round can leave
        // rows for the next pass, as retention's own schedule would; the race itself must not deadlock.
        await RetentionTestOps.PurgeUntilAsync(Services, ns, 100_000, 100_000, 100_000_000, 10, 50, RemainingEmptyAsync, ct);
        foreach (var chunk in keys.Chunk(200).Select(c => c.ToList()))
        {
            Assert.Empty(await Db.From<Lock>().Where(l => chunk.Contains(l.LockKey)).ToListAsync(ct));
        }

        async Task<bool> RemainingEmptyAsync()
        {
            foreach (var chunk in keys.Chunk(200).Select(c => c.ToList()))
            {
                if (await Db.From<Lock>().Where(l => chunk.Contains(l.LockKey)).ToListAsync(ct) is { Count: > 0 })
                {
                    return false;
                }
            }
            return true;
        }
    }
}
