using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for the jobs walk of <c>PurgeExpiredJobs</c>: the job delete seeks its own namespace's expired jobs
/// through <c>ix_jobs_state_retention</c>, oldest retention date first, a batch at a time after the cursor the call
/// before it answered. Each fact seeds a namespace of its own, with jobs expiring at chosen whole seconds, so the walk's
/// order and bounds are the facts' own; the cutoff sits a minute past the database clock.
/// </summary>
[ConformanceSpec(
    "purge-expired-jobs.walk",
    "Purge walks its namespace's expired jobs from the oldest retention date",
    Area = "Retention",
    Contract = "The jobs walk reads only its namespace's expired jobs, oldest retention date first, steps over the ones its guards keep, and deletes the rest a batch at a time."
)]
[Deploys("add-numbers")]
public sealed class PurgeExpiredJobsWalkSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeExpiredJobsWalkSpec>>
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    [Fact(DisplayName = "The walk examines only the expired jobs, oldest first, however many the namespace still keeps")]
    public async Task The_walk_examines_only_the_expired_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(Unique("walk"), ct);
        var kept = new List<long>();
        for (var job = 0; job < 20; job++)
        {
            kept.Add(await SeedJobAsync(ns, definition, now + Hour + TimeSpan.FromMinutes(job), ct));
        }
        var expired = new[]
        {
            await SeedJobAsync(ns, definition, now - (3 * Hour), ct),
            await SeedJobAsync(ns, definition, now - (2 * Hour), ct),
            await SeedJobAsync(ns, definition, now - Hour, ct),
        };

        // One job a batch: each full batch answers the job it examined, so the answers name every job the walk read.
        var walk = await WalkToTheEndAsync(ns, batchSize: 1, ct);

        Assert.Equal(expired, walk.Examined);
        Assert.Equal(3, walk.Purged);
        Assert.Equal(kept.Order(), (await JobsOfAsync(ns, ct)).Order());
    }

    [Fact(DisplayName = "A full batch answers its last examined job, and a shorter one at the end answers no cursor")]
    public async Task A_full_batch_answers_its_last_examined_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(Unique("walk"), ct);
        await SeedJobAsync(ns, definition, now - (3 * Hour), ct);
        var second = await SeedJobAsync(ns, definition, now - (2 * Hour), ct);
        await SeedJobAsync(ns, definition, now - Hour, ct);

        var first = await WalkAsync(ns, batchSize: 2, after: null, ct);
        var last = await WalkAsync(ns, batchSize: 2, first.Next, ct);

        Assert.Equal((2, new RetentionCursor((now - (2 * Hour)).Ticks, second)), (first.Purged, first.Next));
        Assert.Equal((1, (RetentionCursor?)null), (last.Purged, last.Next));
        Assert.Empty(await JobsOfAsync(ns, ct));
    }

    [Fact(DisplayName = "The walk steps over the jobs its guards keep, and the next call deletes the expired job behind them")]
    public async Task The_walk_steps_over_the_jobs_its_guards_keep()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(Unique("walk"), ct);
        // A finished child of a parent still waiting stays for the parent's replay, and a job stays while an alert of it is
        // undelivered; both expired first, so they sit at the old end of the index.
        var parent = await SeedJobAsync(ns, definition, retentionUntilUtc: null, ct, JobStatusCode.Suspended);
        var child = await SeedJobAsync(ns, definition, now - (3 * Hour), ct, parentJobId: parent);
        var alerted = await SeedJobAsync(ns, definition, now - (2 * Hour), ct);
        await AlertTestOps.RaiseAsync(
            Services,
            (await Db.From<NamespaceEntity>().Where(n => n.Id == ns).SingleOrDefaultAsync(ct))!.NamespaceName,
            alerted,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "walk",
            message: "walk",
            channelName: "default",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            ct
        );
        var expired = await SeedJobAsync(ns, definition, now - Hour, ct);

        // One job a batch: a batch its guards keep whole deletes nothing and still answers where the walk goes on.
        var walked = new List<PurgeExpiredJobsResponse>();
        RetentionCursor? cursor = null;
        for (var call = 0; call < 3; call++)
        {
            walked.Add(await WalkAsync(ns, batchSize: 1, cursor, ct));
            cursor = walked[^1].Next;
        }

        Assert.Equal([(0, child), (0, alerted), (1, expired)], walked.Select(w => (w.Purged, w.Next?.JobId ?? 0)));
        Assert.Equal(new[] { parent, child, alerted }.Order(), (await JobsOfAsync(ns, ct)).Order());
    }

    [Fact(DisplayName = "Jobs that expire at one instant, more of them than a batch, are all deleted")]
    public async Task Jobs_that_share_an_instant_are_all_deleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(Unique("walk"), ct);
        // A batch completion stamps every row with one instant, so the walk stops and resumes inside an instant, by job id.
        for (var job = 0; job < 12; job++)
        {
            await SeedJobAsync(ns, definition, now - Hour, ct);
        }

        var walk = await WalkToTheEndAsync(ns, batchSize: 5, ct);

        Assert.Equal(12, walk.Purged);
        Assert.Empty(await JobsOfAsync(ns, ct));
    }

    [Fact(DisplayName = "An expired job is deleted by the first batch however many expired jobs other namespaces hold")]
    public async Task Other_namespaces_jobs_are_never_walked()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = await NowAsync(ct);
        var (ns, definition) = await SeedNamespaceAsync(Unique("walk"), ct);
        var expired = await SeedJobAsync(ns, definition, now - Hour, ct);
        var (other, otherDefinition) = await SeedNamespaceAsync(Unique("walk-other"), ct);
        for (var job = 0; job < 30; job++)
        {
            await SeedJobAsync(other, otherDefinition, now - (2 * Hour), ct);
        }

        // Thirty jobs of another namespace expired before this one, and a batch here is one job.
        var walked = await WalkAsync(ns, batchSize: 1, after: null, ct);

        Assert.Equal(1, walked.Purged);
        Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == expired).SingleOrDefaultAsync(ct));
    }

    // One walk from the oldest expired job to the end of the range, each call reading on from the last one's answer.
    private async Task<(int Purged, List<long> Examined)> WalkToTheEndAsync(int ns, int batchSize, CancellationToken ct)
    {
        RetentionCursor? cursor = null;
        var (purged, examined) = (0, new List<long>());
        for (var call = 1; call <= 100; call++)
        {
            var walked = await WalkAsync(ns, batchSize, cursor, ct);
            purged += walked.Purged;
            if (walked.Next is null)
            {
                return (purged, examined);
            }

            Assert.True(cursor is null || IsLater(walked.Next, cursor), "the walk only moves to later retention dates");
            examined.Add(walked.Next.JobId);
            cursor = walked.Next;
        }

        Assert.Fail("the walk did not reach the end of the expired range in a hundred calls");
        return default;
    }

    private static bool IsLater(RetentionCursor next, RetentionCursor than) =>
        next.Ticks > than.Ticks || (next.Ticks == than.Ticks && next.JobId > than.JobId);

    // The database clock to the whole second, so a seeded retention date reads back as it was written on every provider.
    private async Task<DateTime> NowAsync(CancellationToken ct)
    {
        var now = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private async Task<IEnumerable<long>> JobsOfAsync(int ns, CancellationToken ct) =>
        (await Db.From<JobEntity>().Where(j => j.NamespaceId == ns).ToListAsync(ct)).Select(j => j.Id);

    private async Task<PurgeExpiredJobsResponse> WalkAsync(int ns, int batchSize, RetentionCursor? after, CancellationToken ct) =>
        await Services
            .GetRequiredService<MaintenanceOperations>()
            .PurgeExpiredJobsAsync(new PurgeExpiredJobsCommand(ns, (await NowAsync(ct)).AddMinutes(1), batchSize, after), ct);

    private async Task<(int NamespaceId, int DefinitionId)> SeedNamespaceAsync(string name, CancellationToken ct)
    {
        var seeder = new ActaTestSeeder(Db);
        var ns = await seeder.SeedJobNamespaceAsync(name, ct: ct);
        return (ns, await seeder.SeedJobDefinitionAsync(ns, Unique("walk-def"), ct: ct));
    }

    // A job as its two rows, finished and expiring at a chosen instant unless told otherwise: the walk reads nothing else.
    private async Task<long> SeedJobAsync(
        int ns,
        int definition,
        DateTime? retentionUntilUtc,
        CancellationToken ct,
        JobStatusCode status = JobStatusCode.Succeeded,
        long? parentJobId = null
    )
    {
        var jobId = await Db.From<JobEntity>()
            .InsertAsync<long>(
                new JobEntity
                {
                    JobRef = Guid.NewGuid(),
                    NamespaceId = ns,
                    DefinitionId = definition,
                    ParentJobId = parentJobId,
                    LineageRootJobId = parentJobId,
                    InputFormatId = 1,
                    Input = [0],
                    EventRecording = EventRecordingCode.Failures,
                },
                ct
            );
        await Db.From<JobStateEntity>()
            .InsertAsync<long>(
                new JobStateEntity
                {
                    Id = jobId,
                    NamespaceId = ns,
                    Status = status,
                    Priority = JobPriorityCode.Normal,
                    ExecutionNumber = 1,
                    RetentionUntilUtc = retentionUntilUtc,
                },
                ct
            );
        return jobId;
    }
}
