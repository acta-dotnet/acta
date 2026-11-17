using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Workers;

/// <summary>
/// Conformance for the release of workers whose process has ended. A release turns a worker that is still Active Dead
/// with a <c>worker.died</c> event and lapses the leases of the jobs it executes, with those jobs' lock holds and
/// concurrency slots, so a recovery pass reclaims them at once; a rate meter's row, a Stopped worker, and another
/// worker's jobs stay as they were. A worker starting on SQLite, where one process owns the file, releases its
/// namespace's workers of this machine whose process has ended, an id now held by a later process included; a server
/// database leaves them to the lease, because replicas sharing a host name keep process ids of their own.
/// </summary>
[ConformanceSpec(
    "release-gone-workers.lapse-leases-and-holds",
    "Releasing an ended worker lapses its jobs' leases and holds at once",
    Area = "Workers",
    Contract = "ReleaseGoneWorkers turns live workers Dead and lapses their jobs' leases, locks and slots, and a SQLite start releases this machine's ended workers."
)]
[Deploys("add-numbers")]
public sealed class ReleaseGoneWorkersSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ReleaseGoneWorkersSpec>>
{
    private static readonly TimeSpan Held = TimeSpan.FromMinutes(10);

    [Fact(
        DisplayName = "A release turns the worker Dead and lapses its jobs' leases, locks, and slots, and leaves rate rows and other workers"
    )]
    public async Task A_release_lapses_the_workers_leases_and_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workers = Services.GetRequiredService<WorkersOperations>();
        var locks = Services.GetRequiredService<LocksOperations>();
        var gone = await StartWorkerAsync("gone-host", processId: 0, ct);
        var live = await StartWorkerAsync("live-host", processId: 0, ct);
        var stopped = await StartWorkerAsync("stopped-host", processId: 0, ct);
        await workers.StopWorkerAsync(ns, stopped, ct);
        var goneJob = await ClaimAsync(ns, gone, ct);
        var liveJob = await ClaimAsync(ns, live, ct);

        var lockKey = $"{ns}.lock.{Unique("ledger")}";
        var rateKey = $"{ns}.rate.{Unique("calls")}.{goneJob}";
        var liveKey = $"{ns}.lock.{Unique("other")}";
        Assert.NotNull((await locks.AcquireLockAsync(lockKey, Held, goneJob, ct)).Token);
        var slot = (await locks.AcquireLockSlotAsync($"{ns}.sem.{Unique("batch")}", 1, Held, goneJob, ct)).Token;
        Assert.NotNull(slot);
        Assert.NotNull((await locks.AcquireLockAsync(rateKey, Held, goneJob, ct)).Token);
        Assert.NotNull((await locks.AcquireLockAsync(liveKey, Held, liveJob, ct)).Token);

        await workers.ReleaseGoneWorkersAsync([gone, stopped], ct);

        // Every hold above runs ten minutes from its write, so half of that tells a lapsed one from a kept one on any
        // database clock. A lapsed lock may already be gone, reaped by a sweep sharing the database.
        var kept = DateTime.UtcNow.AddMinutes(5);
        Assert.Equal(WorkerStatusCode.Dead, await StatusAsync(gone, ct));
        Assert.Equal(WorkerStatusCode.Active, await StatusAsync(live, ct));
        Assert.Equal(WorkerStatusCode.Stopped, await StatusAsync(stopped, ct));
        Assert.Single(await DiedAsync(gone, ct));
        Assert.Empty(await DiedAsync(stopped, ct));
        Assert.True((await ReadJobAsync(goneJob, ct)).LeaseExpiresAtUtc < kept);
        Assert.True((await ReadJobAsync(liveJob, ct)).LeaseExpiresAtUtc > kept);
        Assert.False(await ExpiresAsync(lockKey, ct) > kept);
        Assert.False(await ExpiresAsync(slot.Value.Key, ct) > kept);
        Assert.True(await ExpiresAsync(rateKey, ct) > kept);
        Assert.True(await ExpiresAsync(liveKey, ct) > kept);

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(goneJob, ct)).Status);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(liveJob, ct)).Status);
    }

    [Fact(
        DisplayName = "A start on SQLite releases this machine's workers whose process has ended and reclaims their jobs; a server database leaves them to the lease"
    )]
    public async Task A_start_releases_this_machines_ended_workers_on_sqlite()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var host = Environment.MachineName;

        // No process has the largest id. A row older than this process is not this process's, though it has its id.
        var ended = await StartWorkerAsync(host, int.MaxValue, ct);
        var reused = await StartWorkerAsync(host, Environment.ProcessId, ct);
        var aDayAgo = DateTime.UtcNow.AddDays(-1);
        await Db.From<WorkerEntity>().Where(w => w.Id == reused).UpdateOnlyAsync(() => new WorkerEntity { CreatedAtUtc = aDayAgo }, ct);
        var running = await StartWorkerAsync(host, Environment.ProcessId, ct);
        var elsewhere = await StartWorkerAsync(Unique("another-host"), int.MaxValue, ct);
        var job = await ClaimAsync(ns, ended, ct);

        await using (var replica = Replica())
        {
            await CatalogTestOps.DeployAndStartAsync(replica, replica.GetServices<WorkerRuntime>().Single(), ct);
        }

        var released = Db.Provider == DbProvider.Sqlite;
        var gone = released ? WorkerStatusCode.Dead : WorkerStatusCode.Active;
        Assert.Equal(gone, await StatusAsync(ended, ct));
        Assert.Equal(gone, await StatusAsync(reused, ct));
        Assert.Equal(WorkerStatusCode.Active, await StatusAsync(running, ct));
        Assert.Equal(WorkerStatusCode.Active, await StatusAsync(elsewhere, ct));
        Assert.Equal(released ? JobStatusCode.Ready : JobStatusCode.Executing, (await ReadJobAsync(job, ct)).Status);
        if (released)
        {
            var orphaned = await ReadSingleEventAsync(job, EventCode.JobExecutionOrphaned, ct);
            Assert.Equal(EventReason.ProcessEnded, EventDetail(orphaned, "reason"));
            Assert.Equal("Worker's process ended; released when this host started.", orphaned.EventMessage);
        }
    }

    private async Task<int> StartWorkerAsync(string hostName, int processId, CancellationToken ct) =>
        (
            await WorkerTestOps.StartAsync(
                Services,
                TestNamespace,
                ownerTeam: "test",
                description: null,
                hostName: hostName,
                deploymentVersion: "test",
                engineVersion: null,
                dotnetVersion: null,
                processId: processId,
                maxConcurrentExecutors: 1,
                ct
            )
        ).WorkerId;

    /// <summary>An add-numbers job claimed by <paramref name="workerId"/> under a lease ten minutes long.</summary>
    private async Task<long> ClaimAsync(int ns, int workerId, CancellationToken ct)
    {
        var job = (await ChaosSpecHelpers.EnqueueAddNumbersAsync(Services, Jobs, TestNamespace, ct)).JobId;
        await SpecWaits.RepeatUntilAsync(
            () =>
                Services
                    .GetRequiredService<ExecutionOperations>()
                    .ClaimJobAsync(new ClaimOptions(ns, workerId, MaxBatch: 1), (int)Held.TotalSeconds, job, ct),
            answer => answer.Jobs.Count > 0,
            ct
        );
        return job;
    }

    private async Task<WorkerStatusCode> StatusAsync(int workerId, CancellationToken ct) =>
        (await Db.From<WorkerEntity>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct))!.Status;

    private Task<IReadOnlyList<EventEntity>> DiedAsync(int workerId, CancellationToken ct) =>
        Db.From<EventEntity>().Where(e => e.WorkerId == workerId && e.EventCode == EventCode.WorkerDied).ToListAsync(ct);

    private async Task<DateTime?> ExpiresAsync(string lockKey, CancellationToken ct) =>
        (await Db.From<LockEntity>().Where(l => l.LockKey == lockKey).SingleOrDefaultAsync(ct))?.ExpiresAtUtc;

    /// <summary>A second process of this build in the namespace, with crash recovery on, which the release belongs to.</summary>
    private ServiceProvider Replica()
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<JobsOf<ReleaseGoneWorkersSpec>>(TestNamespace, ownerTeam: "test", description: GetType().FullName);
        });
        services.Configure<JobsOptions>(o => o.RunSystemJobs = true);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
