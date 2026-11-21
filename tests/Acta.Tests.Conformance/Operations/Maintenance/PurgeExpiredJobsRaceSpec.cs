using System.Data.Common;
using System.Diagnostics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// The job delete races the writers that move a job in and out of <c>ix_jobs_state_retention</c>: landings that finish
/// jobs with a retention date already past, and restarts that clear it, while the delete runs batch after batch. The
/// delete reads its candidates from the index without update locks and locks each through its state row and then its
/// job row, skipping a row another transaction holds, the order a landing and a restart take, so no two of them wait on
/// each other in a cycle. Deadlock retry is off, so a deadlock victim on either side fails the fact.
/// </summary>
[ConformanceSpec(
    "purge-expired-jobs.landing-race",
    "The job delete races landings and restarts without a deadlock",
    Area = "Retention",
    Contract = "Deleting expired jobs while other jobs land past their retention and finished jobs restart never deadlocks, and the delete deletes as it goes."
)]
[Deploys("add-numbers")]
public sealed class PurgeExpiredJobsRaceSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeExpiredJobsRaceSpec>>
{
    private const int Landers = 4;
    private const int BatchSize = 25;
    private static readonly TimeSpan RaceFor = TimeSpan.FromSeconds(3);

    // A race with deadlock retry off needs its tables to itself (CONTRIBUTING.md, "Tests that run in parallel").
    protected override bool OwnSchema => true;

    [Fact(DisplayName = "The job delete racing landings past retention and restarts of finished jobs never deadlocks")]
    public async Task The_job_delete_races_landings_and_restarts()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = Assert
            .IsType<WorkerEntity>(await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct))
            .Id;
        // Every landing in the race is stamped an hour before its own instant, so it is past this cutoff as it lands.
        var cutoff = (await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct)).AddMinutes(1);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var race = new Stopwatch();
        var landers = Enumerable.Range(0, Landers).Select(_ => Run(start, () => LandAndRestartAsync(ns, workerId, race, ct), ct)).ToList();
        var purge = Run(start, () => PurgeBackToBackAsync(ns, cutoff, race, ct), ct);
        race.Start();
        start.SetResult();
        var raced = await Task.WhenAll(landers);
        var (landed, restarted, purged) = (raced.Sum(r => r.Landed), raced.Sum(r => r.Restarted), await purge);

        TestContext.Current.TestOutputHelper?.WriteLine($"{landed} landings, {restarted} restarts, {purged} jobs deleted");
        Assert.True(purged > 0, $"the job delete deleted nothing while {landed} jobs landed past their retention");
        // SQLite's one writer runs the queued delete between each landing and its restart, so there the delete takes every
        // finished job first and the restart finds it gone.
        Assert.True(
            Db.Provider == DbProvider.Sqlite || restarted > 0,
            $"no restart of a finished job raced the delete in {landed} landings"
        );
    }

    // Each job lands past its retention, restarts, and lands again, unless the delete took it in between.
    private async Task<(int Landed, int Restarted)> LandAndRestartAsync(int ns, int workerId, Stopwatch race, CancellationToken ct)
    {
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var request = new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2)));
        var (landed, restarted) = (0, 0);
        while (race.Elapsed < RaceFor)
        {
            try
            {
                var job = (await Jobs.EnqueueAsync(request, ct)).JobId;
                if (!await LandPastRetentionAsync(execution, ns, workerId, leaseTtl, job, ct))
                {
                    continue;
                }

                landed++;
                if ((await Jobs.RestartAsync(JobLookup.ById(job), ct: ct)).Action == ControlAction.Applied)
                {
                    restarted++;
                    landed += await LandPastRetentionAsync(execution, ns, workerId, leaseTtl, job, ct) ? 1 : 0;
                }
            }
            catch (DbException ex) when (Fixture.IsTransient(ex)) { }
        }

        return (landed, restarted);
    }

    private static async Task<bool> LandPastRetentionAsync(
        ExecutionOperations execution,
        int ns,
        int workerId,
        int leaseTtl,
        long jobId,
        CancellationToken ct
    )
    {
        if (await execution.ClaimJobAsync(ns, workerId, leaseTtl, jobId, ct) is not [var claimed])
        {
            return false;
        }

        await execution.CompleteAsync(workerId, CompletionTestOps.Landing(claimed) with { RetentionSeconds = -3600 }, ct);
        return true;
    }

    private async Task<int> PurgeBackToBackAsync(int ns, DateTime cutoff, Stopwatch race, CancellationToken ct)
    {
        var maintenance = Services.GetRequiredService<MaintenanceOperations>();
        var (purged, cursor) = (0, (RetentionCursor?)null);
        while (race.Elapsed < RaceFor)
        {
            try
            {
                var batch = await maintenance.PurgeExpiredJobsAsync(new PurgeExpiredJobsCommand(ns, cutoff, BatchSize, cursor), ct);
                purged += batch.Purged;
                cursor = batch.Next;
            }
            catch (DbException ex) when (Fixture.IsTransient(ex)) { }
        }

        return purged;
    }

    private static Task<T> Run<T>(TaskCompletionSource start, Func<Task<T>> action, CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                await start.Task;
                return await action();
            },
            ct
        );
}
