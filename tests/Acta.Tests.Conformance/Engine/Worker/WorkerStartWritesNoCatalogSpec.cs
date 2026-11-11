using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// A worker start reads the catalog a deploy wrote and writes none of it, whatever its build declares: it takes no
/// schedule lock and leaves the namespace, definition, schedule, cursor, and slot rows as it found them, also when its
/// manifest differs from the deployed one. A deploy does take the namespace's schedule lock exclusive and waits for it.
/// A gate holding that lock shows which side takes it; SQLite takes no such lock, so there only the writes are observed.
/// </summary>
[ConformanceSpec(
    "worker.start-writes-no-catalog",
    "A worker start writes nothing in the catalog and takes no schedule lock",
    Area = "Workers",
    Contract = "A worker start completes under a held schedule lock and leaves the catalog as it was whatever its build declares, and a deploy waits for that lock."
)]
[Deploys("recurring-ping")]
public sealed class WorkerStartWritesNoCatalogSpec : ActaTestBase<ConformanceFixture>
{
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(500);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace) =>
        services.UseActa(j => Fixture.ApplyProvider(j, Schema.SchemaName));

    [Fact(DisplayName = "A start of the deployed build completes under a held schedule lock and writes nothing in the catalog")]
    public async Task A_start_of_the_deployed_build_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("start-same");
        await using var build = Replica<JobsOf<WorkerStartWritesNoCatalogSpec>>(ns);
        var nsId = await DeployAsync(build, ns, ct);
        var before = await SnapshotAsync(nsId, ct);

        await StartsUnderTheLockAsync(build, nsId, ct);

        Assert.Equal(before, await SnapshotAsync(nsId, ct));
    }

    [Fact(DisplayName = "A start whose manifest differs from the deployed one completes under the lock and writes nothing")]
    public async Task A_start_of_a_changed_manifest_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("start-changed");
        await using var deployed = Replica<JobsOf<WorkerStartWritesNoCatalogSpec>>(ns);
        var nsId = await DeployAsync(deployed, ns, ct);
        var before = await SnapshotAsync(nsId, ct);

        await using var changed = Replica<UnscheduledPingManifest>(ns);
        await StartsUnderTheLockAsync(changed, nsId, ct);

        Assert.Equal(before, await SnapshotAsync(nsId, ct));
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, nsId, UnscheduledPingManifest.JobName, ct);
        Assert.Equal(ScheduleStatusCode.Active, Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Status);
    }

    [Fact(DisplayName = "A deploy of a changed manifest waits for the schedule lock and then writes its schedules")]
    public async Task A_deploy_waits_for_the_schedule_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Unique("deploy-changed");
        await using var deployed = Replica<JobsOf<WorkerStartWritesNoCatalogSpec>>(ns);
        var nsId = await DeployAsync(deployed, ns, ct);
        var before = await SnapshotAsync(nsId, ct);

        await using var changed = Replica<UnscheduledPingManifest>(ns);
        Task deploying;
        await using (await ScheduleLockGate.OpenAsync(Db, nsId, exclusive: true, ct))
        {
            deploying = CatalogTestOps.DeployAsync(changed, ct);
            if (Db.Provider != DbProvider.Sqlite)
            {
                await Task.Delay(Blocked, ct);
                Assert.False(deploying.IsCompleted, "the deploy did not wait for the schedule lock");
            }
        }
        await deploying.WaitAsync(SpecWaits.Gate, ct);

        Assert.NotEqual(before, await SnapshotAsync(nsId, ct));
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, nsId, UnscheduledPingManifest.JobName, ct);
        Assert.Equal(ScheduleStatusCode.Orphaned, Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Status);
    }

    private async Task<int> DeployAsync(ServiceProvider build, string ns, CancellationToken ct)
    {
        await CatalogTestOps.DeployAsync(build, ct);
        return await ChaosSpecHelpers.NamespaceIdAsync(Db, ns, ct);
    }

    /// <summary>Starts the build's worker while a gate holds the schedule lock exclusive; the start must not wait for it.</summary>
    private async Task StartsUnderTheLockAsync(ServiceProvider build, int nsId, CancellationToken ct)
    {
        await using (await ScheduleLockGate.OpenAsync(Db, nsId, exclusive: true, ct))
        {
            await build.GetServices<WorkerRuntime>().Single().InitializeAsync(ct).WaitAsync(SpecWaits.Gate, ct);
        }
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
        services.Configure<JobsOptions>(o => o.RunSystemJobs = false);
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>Every version and write stamp a deploy bumps in the namespace: the namespace, definitions, schedules, cursors, and slots.</summary>
    private async Task<string> SnapshotAsync(int nsId, CancellationToken ct)
    {
        var ns = (await Db.From<NamespaceEntity>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct))!;
        var definitions = await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == nsId).ToListAsync(ct);
        var schedules = await Db.From<ScheduleEntity>().Where(s => s.NamespaceId == nsId).ToListAsync(ct);
        var cursors = await Db.From<ScheduleStateEntity>().Where(s => s.NamespaceId == nsId).ToListAsync(ct);
        var slotIds = schedules.Select(s => s.JobId).OfType<long>().Distinct().ToList();
        var slots = await Db.From<JobStateEntity>().Where(r => slotIds.Contains(r.Id)).ToListAsync(ct);
        return string.Join(
            "|",
            [
                $"n{ns.Id}:{ns.Version}:{ns.ModifiedAtUtc:O}:{ns.DeploymentVersion}:{ns.OwnerTeam}:{ns.Description}",
                .. definitions.OrderBy(d => d.Id).Select(d => $"d{d.Id}:{d.Version}:{d.ModifiedAtUtc:O}"),
                .. schedules
                    .OrderBy(s => s.JobId)
                    .ThenBy(s => s.ScheduleName)
                    .Select(s => $"s{s.JobId}.{s.ScheduleName}:{s.Version}:{s.ModifiedAtUtc:O}"),
                .. cursors.OrderBy(s => s.Id).Select(s => $"c{s.Id}:{s.NextOccurrenceAtUtc:O}:{s.ModifiedAtUtc:O}"),
                .. slots.OrderBy(r => r.Id).Select(r => $"j{r.Id}:{r.Version}:{r.ModifiedAtUtc:O}"),
            ]
        );
    }
}
