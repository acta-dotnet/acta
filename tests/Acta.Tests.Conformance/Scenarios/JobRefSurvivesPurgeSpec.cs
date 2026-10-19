using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for public identity across a retention purge: <c>events</c> outlives the <c>job</c> row and keeps
/// the denormalized <c>events.job_ref</c>, while the ref itself resolves only through the job row.
/// Drives a zero-retention job to terminal, purges it with a wide event window (job deleted, events kept),
/// then asserts the timeline reads back by job id with the ref intact and the ref resolves to nothing.
/// </summary>
[ConformanceSpec(
    "job-ref.survives-purge",
    "A purged job's events keep its public ref, which no longer resolves",
    Area = "Retention",
    Contract = "After the job row is purged, its surviving events keep the denormalized job_ref and read back by job id, and the ref resolves to null."
)]
public sealed class JobRefSurvivesPurgeSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // A wide retention window makes the events/alerts/workers sections no-ops, so only the
    // zero-retention job row is deleted, leaving its events behind.
    private const int NoEventPurgeDays = 100_000;
    private const int NoAlertPurgeDays = 100_000;
    private const int NoWorkerPurgeSeconds = 100_000_000;

    [Fact(DisplayName = "After a retention purge the events keep job_ref and the timeline reads by job id, while the ref resolves to null")]
    public async Task Events_keep_the_public_ref_after_the_job_row_is_purged_but_the_ref_no_longer_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // purge-now carries zero retention, so it is immediately deletable once complete.
        var enqueued = await EnqueueAndRunAsync("purge-now", new PurgeProbe("ref"), ct);
        var jobId = enqueued.JobId;
        var jobRef = enqueued.JobRef;

        // Before purge: the job's own events already carry the denormalized public ref.
        var eventsBefore = await Db.From<EventEntity>().Where(e => e.JobId == jobId).ToListAsync(ct);
        Assert.NotEmpty(eventsBefore);
        Assert.All(eventsBefore, e => Assert.Equal<Guid?>(jobRef.Value, e.JobRef));

        // Purge the job row but keep its events (wide event window), sweeping until the row is actually
        // gone. One sweep is not enough on the shared parallel schema: purge_expired_data stages its batch
        // WITH (UPDLOCK, READPAST) / FOR UPDATE SKIP LOCKED, so a row another transaction holds is skipped
        // this pass. Every conformance spec inserts into the same jobs/runtimes tables, so this spec's own
        // zero-retention row can sit under an unrelated transaction's lock at the instant a one-shot purge
        // runs - the sweep then deletes nothing and the job row below survives its own purge. Repeating is
        // what production does too (sys.retention runs on a timer), so the settled outcome is the contract.
        var purge = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            NoEventPurgeDays,
            NoAlertPurgeDays,
            NoWorkerPurgeSeconds,
            1000,
            50,
            async () => await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct) is null,
            ct
        );

        // Deliberately NOT "this call purged exactly one job": which pass did it satisfies the contract
        // either way. The events count is not racy in the same way - the wide event window makes the
        // events section a no-op on every pass of the drain, which is the property asserted here.
        Assert.Equal(0, purge.Events);

        // The heavy row is gone, and the ref resolves only through it. These are settled-state assertions,
        // which is why the sweep above loops until they hold rather than firing once: a READPAST skip would
        // otherwise leave the row alive here and fail an assertion about retention with a lock-timing flake.
        Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Null(await Jobs.GetJobIdAsync(JobLookup.ByRef(jobRef), ct));
        Assert.Null(await Jobs.GetAsync(JobLookup.ByRef(jobRef), ct));

        // The timeline still reads back by job id with the ref intact.
        var timeline = (
            await Services.GetRequiredService<ILedger>().ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 100), ct)
        ).Items;
        Assert.NotEmpty(timeline);
        Assert.All(timeline, row => Assert.Equal<JobRef?>(jobRef, row.JobRef));
    }
}
