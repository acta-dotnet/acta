using Acta.Features.Alerts;
using Acta.Features.Retention;
using Acta.Features.Workers;
using Acta.Relational.Entities;
using Acta.Services.Locks;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for <c>sys.retention</c>'s five sweep sections (<c>purge_expired_data</c>):
/// terminal jobs past <c>retention_until_utc</c> (with CASCADE), <c>events</c> rows past the events
/// window, settled <c>alerts</c> rows past the alert window, Dead <c>workers</c> rows past the
/// worker window, and expired <c>leases</c> lock rows. Event/alert/worker windows are driven to deterministic boundaries by passing a window
/// wide enough to exclude everything (large positive) or a cutoff in the future (negative), so no
/// real-time wait is needed; deletable terminal jobs are produced through the real
/// enqueue/execute/complete path via the zero-retention <c>purge-now</c> probe.
/// </summary>
[ConformanceSpec(
    "purge-expired-data.sweeps",
    "Purge reaps expired jobs events alerts and dead workers within batches",
    Area = "Retention",
    Contract = "Purge deletes terminal jobs with cascade, expired events, settled alerts, Dead workers and expired lock rows, capping each batched section at max iterations.",
    Arrange = "Terminal purge-now jobs, events, settled and in-flight alerts, Dead and Active workers, and expired and live lock rows are seeded.",
    Act = "PurgeExpiredData.Run executes with wide and future-cutoff windows driving each sweep section to a deterministic boundary.",
    Assert = "Expired jobs delete with cascade alongside expired events, settled alerts, Dead workers and expired locks, while everything else survives."
)]
[CoversStoreMethod(typeof(IRetentionStore), nameof(IRetentionStore.PurgeExpiredDataAsync))]
public abstract class PurgeExpiredDataSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Window args that make a section a no-op for the rows produced in these tests: a huge positive
    // retention pushes the cutoff far into the past, so recent rows survive.
    private const int NoEventPurgeDays = 100_000;
    private const int NoAlertPurgeDays = 100_000;
    private const int NoWorkerPurgeSeconds = 100_000_000;

    [Fact(DisplayName = "Job retention deletes job tags but preserves surviving alert and event tags")]
    public async Task Terminal_job_retention_cleans_only_job_owned_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var jobId = (await EnqueueAndRunAsync("purge-now", new PurgeProbe("x"), ct)).JobId;

        // Completed -> a results child row exists and the job is terminal Done.
        Assert.NotNull(await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.NotEmpty(await Db.From<JobResult>().Where(r => r.JobId == jobId).ToListAsync(ct));

        var eventRow = (await Db.From<JobEvent>().Where(e => e.JobId == jobId).ToListAsync(ct)).MaxBy(e => e.Id)!;
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "retention tag",
            "retention tag",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            null,
            ct
        );
        var alertRow = Assert.Single(await Db.From<JobAlert>().Where(a => a.JobId == jobId).ToListAsync(ct));
        await Jobs.Tags.UpsertAsync(TagTarget.ForJob(JobLookup.ById(jobId)), new TagInput("retention", "job"), ct);
        await Jobs.Tags.UpsertAsync(TagTarget.ForEvent(eventRow.Id), new TagInput("retention", "event"), ct);
        await Jobs.Tags.UpsertAsync(TagTarget.ForAlert(alertRow.Id), new TagInput("retention", "alert"), ct);

        var result = await RetentionTestOps.PurgeAsync(
            Services,
            ns,
            NoEventPurgeDays,
            NoAlertPurgeDays,
            NoWorkerPurgeSeconds,
            1000,
            50,
            ct
        );

        Assert.Equal(1, result.Jobs);
        Assert.Null(await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Empty(await Db.From<JobResult>().Where(r => r.JobId == jobId).ToListAsync(ct));
        Assert.Null(await Jobs.Tags.GetAsync(TagTarget.ForJob(JobLookup.ById(jobId)), ct));
        Assert.Equal(
            [new TagItem("retention", "event")],
            Assert.IsType<TagSet>(await Jobs.Tags.GetAsync(TagTarget.ForEvent(eventRow.Id), ct)).Items
        );
        Assert.Equal(
            [new TagItem("retention", "alert")],
            Assert.IsType<TagSet>(await Jobs.Tags.GetAsync(TagTarget.ForAlert(alertRow.Id), ct)).Items
        );
    }

    [Fact(DisplayName = "A future-retention job stamped with the default window survives the purge")]
    public async Task Future_retention_job_survives_and_is_stamped_with_the_default_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var jobId = (await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct)).JobId;

        // add-numbers carries no [Job(JobRetention=...)], so completion stamps the 90-day default.
        var job = await ReadJobAsync(jobId, ct);
        Assert.NotNull(job!.RetentionUntilUtc);
        Assert.InRange(job.RetentionUntilUtc!.Value, DateTime.UtcNow.AddDays(89), DateTime.UtcNow.AddDays(91));

        var result = await RetentionTestOps.PurgeAsync(
            Services,
            ns,
            NoEventPurgeDays,
            NoAlertPurgeDays,
            NoWorkerPurgeSeconds,
            1000,
            50,
            ct
        );

        Assert.Equal(0, result.Jobs);
        Assert.NotNull(await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "Expired events are deleted and recent events are kept")]
    public async Task Expired_events_are_deleted_and_recent_events_survive()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Drive one job (future retention) so claim/start/finish events exist in the namespace.
        await EnqueueAndRunAsync("add-numbers", new AddNumbers(1, 1), ct);
        var before = await Db.From<JobEvent>().Where(e => e.NamespaceId == ns).ToListAsync(ct);
        Assert.NotEmpty(before);
        var taggedEvent = before.MaxBy(e => e.Id)!;
        await Jobs.Tags.UpsertAsync(TagTarget.ForEvent(taggedEvent.Id), new TagInput("retention"), ct);

        // A wide retention window leaves recent events untouched.
        var keep = await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(0, keep.Events);
        Assert.NotEmpty(await Db.From<JobEvent>().Where(e => e.NamespaceId == ns).ToListAsync(ct));
        Assert.NotNull(await Jobs.Tags.GetAsync(TagTarget.ForEvent(taggedEvent.Id), ct));

        // A negative window puts the cutoff in the future, so every event is past it.
        var purged = await RetentionTestOps.PurgeAsync(Services, ns, -1, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(before.Count, purged.Events);
        Assert.Empty(await Db.From<JobEvent>().Where(e => e.NamespaceId == ns).ToListAsync(ct));
        Assert.Empty(await Db.From<Tag>().Where(t => t.ScopeCode == TagScopeCode.Event && t.ScopeId == taggedEvent.Id).ToListAsync(ct));
    }

    [Fact(DisplayName = "A Dead worker is reaped and an Active worker is kept")]
    public async Task Dead_worker_is_deleted_and_active_worker_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        Assert.Equal(WorkerStatusCode.Active, worker!.Status);
        await Jobs.Tags.UpsertAsync(TagTarget.ForWorker(worker.Id), new TagInput("retention"), ct);

        // Active workers are never purged, even with the cutoff in the future.
        var active = await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, -1, 1000, 50, ct);
        Assert.Equal(0, active.Workers);
        Assert.NotNull(await Db.From<JobWorker>().Where(w => w.Id == worker.Id).SingleOrDefaultAsync(ct));
        Assert.NotNull(await Jobs.Tags.GetAsync(TagTarget.ForWorker(worker.Id), ct));

        // Retire it: age last_seen past a positive window, then the global sweep flips it to Dead.
        var agedAt = DateTime.UtcNow.AddHours(-1);
        await Db.From<JobWorker>().Where(w => w.Id == worker.Id).UpdateOnlyAsync(() => new JobWorker { LastSeenAtUtc = agedAt }, ct);
        await Services.GetRequiredService<IWorkerStore>().MarkDeadWorkersAsync(30, ct);
        var purged = await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, -1, 1000, 50, ct);
        Assert.Equal(1, purged.Workers);
        Assert.Null(await Db.From<JobWorker>().Where(w => w.Id == worker.Id).SingleOrDefaultAsync(ct));
        Assert.Empty(await Db.From<Tag>().Where(t => t.ScopeCode == TagScopeCode.Worker && t.ScopeId == worker.Id).ToListAsync(ct));
    }

    [Fact(DisplayName = "A settled alert past the window is deleted and an in-flight alert is kept")]
    public async Task Settled_alert_past_window_is_deleted_and_inflight_alert_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Null deduplication keys always insert, so these land as two distinct rows: one settled (Delivered),
        // one still in flight (Pending).
        await RaiseAlertAsync(Db, TestNamespace, AlertDeliveryStatusCode.Delivered, ct);
        await RaiseAlertAsync(Db, TestNamespace, AlertDeliveryStatusCode.Pending, ct);
        var seeded = await Db.From<JobAlert>().Where(a => a.NamespaceId == ns).ToListAsync(ct);
        var settled = seeded.Single(a => a.DeliveryStatusCode == AlertDeliveryStatusCode.Delivered);
        var inFlight = seeded.Single(a => a.DeliveryStatusCode == AlertDeliveryStatusCode.Pending);
        await Jobs.Tags.UpsertAsync(TagTarget.ForAlert(settled.Id), new TagInput("retention", "delete"), ct);
        await Jobs.Tags.UpsertAsync(TagTarget.ForAlert(inFlight.Id), new TagInput("retention", "keep"), ct);

        // A wide window leaves both rows untouched.
        var keep = await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(0, keep.Alerts);
        Assert.Equal(2, (await Db.From<JobAlert>().Where(a => a.NamespaceId == ns).ToListAsync(ct)).Count);

        // A future cutoff makes every row past the window, but only the settled one is eligible - the
        // in-flight (Pending) delivery is never purged regardless of age.
        var purged = await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, -1, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(1, purged.Alerts);
        var survivors = await Db.From<JobAlert>().Where(a => a.NamespaceId == ns).ToListAsync(ct);
        Assert.Equal(AlertDeliveryStatusCode.Pending, Assert.Single(survivors).DeliveryStatusCode);
        Assert.Empty(await Db.From<Tag>().Where(t => t.ScopeCode == TagScopeCode.Alert && t.ScopeId == settled.Id).ToListAsync(ct));
        Assert.Equal(
            [new TagItem("retention", "keep")],
            Assert.IsType<TagSet>(await Jobs.Tags.GetAsync(TagTarget.ForAlert(inFlight.Id), ct)).Items
        );
    }

    private Task<int> RaiseAlertAsync(IDbSession db, string jobNamespace, AlertDeliveryStatusCode delivery, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            jobNamespace,
            jobId: null,
            AlertOriginCode.Manual,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "t",
            message: "m",
            channelName: "default",
            delivery,
            deduplicationKey: null,
            dedupeWindowStartUtc: null,
            ct
        );

    [Fact(DisplayName = "An expired lock row is reaped and a live lock is kept")]
    public async Task Expired_lock_row_is_reaped_and_a_live_lock_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Per-test keys (TestKey needle): the live row outlives the test by its TTL in the shared
        // schema, so a constant key would block re-acquisition on the next run; uniqueness also keeps
        // concurrent specs from stealing them. One lock held with a long TTL, one whose negative TTL
        // puts its expiry in the past, making it immediately reapable.
        var liveKey = TestKey("reap-spec.live");
        var deadKey = TestKey("reap-spec.dead");
        var liveToken = await Services
            .GetRequiredService<ILockStore>()
            .TryAcquireAsync(liveKey, TimeSpan.FromSeconds(3600), ownerJobId: -1, ct);
        Assert.NotNull(liveToken);
        Assert.NotNull(
            await Services.GetRequiredService<ILockStore>().TryAcquireAsync(deadKey, TimeSpan.FromSeconds(-1), ownerJobId: -1, ct)
        );

        try
        {
            await RetentionTestOps.PurgeAsync(
                Services,
                ns,
                NoEventPurgeDays,
                NoAlertPurgeDays,
                NoWorkerPurgeSeconds,
                1000,
                50,
                ct
            );

            // The reap is global (leases has no namespace), so a concurrent spec's purge may sweep the
            // dead row first and leave this call's count at 0 - assert only the per-key outcome.
            Assert.Null(await Db.From<Lease>().Where(l => l.LeaseKey == deadKey).SingleOrDefaultAsync(ct));
            Assert.NotNull(await Db.From<Lease>().Where(l => l.LeaseKey == liveKey).SingleOrDefaultAsync(ct));
        }
        finally
        {
            // Drop the held row so the shared schema carries no hour-long lease out of this test.
            await Services.GetRequiredService<ILockStore>().ReleaseAsync(liveToken!.Value, ct);
        }
    }

    [Fact(DisplayName = "Batching caps a single call at max iterations and a full run clears the rest")]
    public async Task Batching_caps_a_single_call_at_max_iterations_and_a_full_run_clears_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        await EnqueueAndRunAsync("purge-now", new PurgeProbe("a"), ct);
        await EnqueueAndRunAsync("purge-now", new PurgeProbe("b"), ct);

        // batch 1 x 1 iteration deletes exactly one of the two due jobs.
        var first = await RetentionTestOps.PurgeAsync(
            Services,
            ns,
            NoEventPurgeDays,
            NoAlertPurgeDays,
            NoWorkerPurgeSeconds,
            batchSize: 1,
            maxIterations: 1,
            ct
        );
        Assert.Equal(1, first.Jobs);

        // A full run clears the remainder.
        var rest = await RetentionTestOps.PurgeAsync(
            Services,
            ns,
            NoEventPurgeDays,
            NoAlertPurgeDays,
            NoWorkerPurgeSeconds,
            batchSize: 1000,
            maxIterations: 50,
            ct
        );
        Assert.Equal(1, rest.Jobs);
    }
}
