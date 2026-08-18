using Acta.Relational.Entities;
using Acta.Runtime.Maintenance;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for public-identity survival across purge: <c>events</c> outlives the <c>job</c> row,
/// and the denormalized <c>events.job_ref</c> keeps the public ref resolvable after the job is purged.
/// Drives a zero-retention job to terminal, purges it with a wide event window (job deleted, events kept),
/// then asserts the ref still resolves and its timeline reads back.
/// </summary>
[ConformanceSpec(
    "job-ref.survives-purge",
    "A purged job's public ref still resolves to its surviving event timeline",
    Area = "Retention",
    Contract = "After the job row is purged, the denormalized job_ref on surviving events rows still resolves the public ref to its historical id and timeline.",
    Arrange = "A zero-retention job definition exists and purge retention windows keep events while deleting the job row.",
    Act = "The job completes, PurgeExpiredData runs, then the public ref is resolved and its events listed.",
    Assert = "The job row is gone but the denormalized job_ref on surviving events still resolves the ref to its historical id and timeline."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ResolveJobIdByRefAsync))]
[CoversStoreMethod(typeof(IEventStore), nameof(IEventStore.ListEventsAsync))]
[CoversStoreMethod(typeof(IRetentionStore), nameof(IRetentionStore.PurgeExpiredDataAsync))]
public abstract class JobRefSurvivesPurgeSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // A wide retention window makes the events/alerts/workers sections no-ops, so only the
    // zero-retention job row is deleted, leaving its events behind.
    private const int NoEventPurgeDays = 100_000;
    private const int NoAlertPurgeDays = 100_000;
    private const int NoWorkerPurgeSeconds = 100_000_000;

    [Fact(
        DisplayName = "After purge the job row is gone and GetAsync by ref is null, but ResolveJobIdByRef falls back to the surviving events that carry the denormalized job_ref"
    )]
    public async Task Public_ref_resolves_to_surviving_events_after_the_job_row_is_purged()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // purge-now carries zero retention, so it is immediately deletable once complete.
        var enqueued = await EnqueueAndRunAsync("purge-now", new PurgeProbe("ref"), ct);
        var jobId = enqueued.JobId;
        var jobRef = enqueued.JobRef;

        // Before purge: the job's own events already carry the denormalized public ref.
        var eventsBefore = await Db.From<JobEvent>().Where(e => e.JobId == jobId).ToListAsync(ct);
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
            async () => await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct) is null,
            ct
        );

        // Deliberately NOT "this call purged exactly one job": the count says which pass did it, and any
        // pass doing it satisfies the contract. The events count is not racy in the same way - the wide
        // event window makes the events section a no-op on every pass, which is the property asserted here.
        Assert.Equal(0, purge.Events);

        // Force a fresh insert after purge. SQLite used to reuse the deleted highest row id here,
        // making the old event fallback resolve to a live unrelated row.
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var replacementPayload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(0, 0));
        var replacement = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", replacementPayload, null, null, null),
            ct
        );
        Assert.NotEqual(jobId, replacement.JobId);

        // The heavy row is gone; GetAsync by ref returns null. Both of these are settled-state assertions,
        // which is why the sweep above loops until they hold rather than firing once: a READPAST skip would
        // otherwise leave the row alive here and fail an assertion about retention with a lock-timing flake.
        Assert.Null(await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Null(await Jobs.GetAsync(JobLookup.ByRef(jobRef), ct));

        // But the public ref still resolves to the historical id via the surviving ledger,
        // and the timeline reads back with the ref intact.
        Assert.Equal(jobId, await Services.GetRequiredService<IJobStore>().ResolveJobIdByRefAsync(jobRef.Value, ct));
        var timeline = (
            await Services
                .GetRequiredService<IEventStore>()
                .ListEventsAsync(
                    new EventPageRequest(jobId, null, null, null, null, null, null, null, null, null, null, null, null, null, 100, false),
                    ct
                )
        ).Rows;
        Assert.NotEmpty(timeline);
        Assert.All(timeline, row => Assert.Equal<JobRef?>(jobRef, row.JobRef));
    }
}
