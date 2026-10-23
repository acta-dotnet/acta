using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// A worker start stores its catalog's hash on the namespace in the transaction that registers its recurring slots. A
/// later start of the same build, whose stored cursors and slots it would leave as they are, skips that registration and
/// its exclusive schedule lock. A gate holding the namespace's schedule lock exclusive shows which starts take it; SQLite
/// takes no such lock, so there only the writes are observed.
/// </summary>
[ConformanceSpec(
    "worker.schedule-registration-skip",
    "A replica of the registered build skips the schedule registration",
    Area = "Workers",
    Contract = "A start whose catalog hash is stored and whose plan moves no cursor or slot takes no schedule lock, and a changed manifest or a missed occurrence registers."
)]
public sealed class ScheduleRegistrationSkipSpec : ActaTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(500);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace) =>
        services.UseActa(j => Fixture.ApplyProvider(j, Schema.SchemaName));

    [Fact(DisplayName = "A second worker of the same build registers nothing and takes no schedule lock")]
    public async Task A_second_worker_of_the_same_build_skips_the_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("skip-same");
        await using var first = Replica<TestJobsManifest>(ns);
        var firstRuntime = first.GetServices<WorkerRuntime>().Single();
        await firstRuntime.InitializeAsync(ct);
        var nsId = firstRuntime.RegisteredNamespaceIds[ns];
        Assert.NotNull(await StoredHashAsync(nsId, ct));
        var before = await SnapshotAsync(nsId, ct);

        await using var second = Replica<TestJobsManifest>(ns);
        var secondRuntime = second.GetServices<WorkerRuntime>().Single();
        await using (await ScheduleLockGate.OpenAsync(Db, nsId, exclusive: true, ct))
        {
            await secondRuntime.InitializeAsync(ct).WaitAsync(SpecWaits.Gate, ct);
        }

        Assert.Equal(before, await SnapshotAsync(nsId, ct));
        Assert.Equal(firstRuntime.RecurringSlotJobIds.Order(), secondRuntime.RecurringSlotJobIds.Order());
    }

    [Fact(DisplayName = "A changed manifest takes the full registration and waits for the schedule lock")]
    public async Task A_changed_manifest_takes_the_full_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("skip-changed");
        await using var first = Replica<TestJobsManifest>(ns);
        var firstRuntime = first.GetServices<WorkerRuntime>().Single();
        await firstRuntime.InitializeAsync(ct);
        var nsId = firstRuntime.RegisteredNamespaceIds[ns];
        var hash = await StoredHashAsync(nsId, ct);

        await using var changed = Replica<UnscheduledPingManifest>(ns);
        await StartsBehindTheLockAsync(changed, nsId, "the changed manifest did not wait for the schedule lock", ct);

        Assert.NotEqual(hash, await StoredHashAsync(nsId, ct));
        var slotId = await SlotIdAsync(nsId, UnscheduledPingManifest.JobName, ct);
        Assert.Equal(ScheduleStatusCode.Orphaned, Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Status);
    }

    [Fact(DisplayName = "A start of the same build after a missed occurrence still reconciles it under the misfire policy")]
    public async Task A_missed_occurrence_takes_the_full_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("skip-missed");
        await using var first = Replica<TestJobsManifest>(ns);
        var firstRuntime = first.GetServices<WorkerRuntime>().Single();
        await firstRuntime.InitializeAsync(ct);
        var nsId = firstRuntime.RegisteredNamespaceIds[ns];
        var slotId = await SlotIdAsync(nsId, UnscheduledPingManifest.JobName, ct);

        // The whole fleet was down past the slot's occurrence, which its Skip policy drops at the next start.
        var missed = DateTime.UtcNow.AddHours(-2);
        await Db.From<ScheduleStateEntity>()
            .Where(s => s.JobId == slotId)
            .UpdateOnlyAsync(() => new ScheduleStateEntity { NextOccurrenceAtUtc = missed }, ct);
        await Db.From<JobStateEntity>().Where(r => r.Id == slotId).UpdateOnlyAsync(() => new JobStateEntity { NextRunAtUtc = missed }, ct);

        await using var second = Replica<TestJobsManifest>(ns);
        await StartsBehindTheLockAsync(second, nsId, "the start skipped a registration that had a missed occurrence to drop", ct);

        Assert.True(Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    /// <summary>Starts the replica while a gate holds the schedule lock, and on the servers asserts the start waits for it.</summary>
    private async Task StartsBehindTheLockAsync(ServiceProvider replica, int nsId, string waitMessage, CancellationToken ct)
    {
        Task registering;
        await using (await ScheduleLockGate.OpenAsync(Db, nsId, exclusive: true, ct))
        {
            registering = replica.GetServices<WorkerRuntime>().Single().InitializeAsync(ct);
            if (Db.Provider != DbProvider.Sqlite)
            {
                await Task.Delay(Blocked, ct);
                Assert.False(registering.IsCompleted, waitMessage);
            }
        }
        await registering.WaitAsync(SpecWaits.Gate, ct);
    }

    private ServiceProvider Replica<TManifest>(string ns)
        where TManifest : class, IJobManifest
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TManifest>(ns, ownerTeam: "test", description: "replica");
        });
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterSystemJobs = false;
            o.ManifestGenerationUtc = Generation;
        });
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<long> SlotIdAsync(int nsId, string jobName, CancellationToken ct) =>
        (await Db.From<JobEntity>().Where(j => j.NamespaceId == nsId && j.DeduplicationKey == jobName).SingleOrDefaultAsync(ct))!.Id;

    private async Task<string?> StoredHashAsync(int nsId, CancellationToken ct) =>
        (await Db.From<NamespaceEntity>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct))!.CatalogHash;

    /// <summary>Every version and write stamp a registration bumps in the namespace: definitions, schedules, cursors, and slots.</summary>
    private async Task<string> SnapshotAsync(int nsId, CancellationToken ct)
    {
        var definitions = await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == nsId).ToListAsync(ct);
        var schedules = await Db.From<ScheduleEntity>().Where(s => s.NamespaceId == nsId).ToListAsync(ct);
        var cursors = await Db.From<ScheduleStateEntity>().Where(s => s.NamespaceId == nsId).ToListAsync(ct);
        var slotIds = schedules.Select(s => s.JobId).Distinct().ToList();
        var slots = await Db.From<JobStateEntity>().Where(r => slotIds.Contains(r.Id)).ToListAsync(ct);
        return string.Join(
            "|",
            [
                .. definitions.OrderBy(d => d.Id).Select(d => $"d{d.Id}:{d.Version}:{d.ModifiedAtUtc:O}"),
                .. schedules
                    .OrderBy(s => s.JobId)
                    .ThenBy(s => s.ScheduleName)
                    .Select(s => $"s{s.JobId}.{s.ScheduleName}:{s.Version}:{s.ModifiedAtUtc:O}"),
                .. cursors
                    .OrderBy(s => s.JobId)
                    .ThenBy(s => s.ScheduleName)
                    .Select(s => $"c{s.JobId}.{s.ScheduleName}:{s.NextOccurrenceAtUtc:O}:{s.ModifiedAtUtc:O}"),
                .. slots.OrderBy(r => r.Id).Select(r => $"j{r.Id}:{r.Version}:{r.ModifiedAtUtc:O}"),
            ]
        );
    }
}
