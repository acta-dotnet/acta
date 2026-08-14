using Acta.Relational.Entities;
using Acta.Runtime.Maintenance;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Locks;
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

        // Completed -> a results child row exists and the job is terminal Succeeded.
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
        await Operations.Tags.UpsertAsync(TagTarget.ForJob(JobLookup.ById(jobId)), new TagInput("retention", "job"), ct: ct);
        await Operations.Tags.UpsertAsync(TagTarget.ForEvent(eventRow.Id), new TagInput("retention", "event"), ct: ct);
        await Operations.Tags.UpsertAsync(TagTarget.ForAlert(alertRow.Id), new TagInput("retention", "alert"), ct: ct);

        // Drain rather than assert a single call deletes it: the sweep selects WITH (UPDLOCK, READPAST),
        // so a row contended by the live claim loop is skipped rather than waited for. The count still
        // has to come to exactly one across the whole drain, because only this job is expired.
        var purgedJobs = 0;
        for (var pass = 1; pass <= 20 && purgedJobs == 0; pass++)
        {
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
            purgedJobs += result.Jobs;
        }

        Assert.Equal(1, purgedJobs);
        Assert.Null(await Db.From<Job>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Empty(await Db.From<JobResult>().Where(r => r.JobId == jobId).ToListAsync(ct));
        Assert.Null(await Operations.Tags.GetAsync(TagTarget.ForJob(JobLookup.ById(jobId)), ct));
        Assert.Equal(
            [new TagItem("retention", "event")],
            Assert.IsType<TagSet>(await Operations.Tags.GetAsync(TagTarget.ForEvent(eventRow.Id), ct)).Items
        );
        Assert.Equal(
            [new TagItem("retention", "alert")],
            Assert.IsType<TagSet>(await Operations.Tags.GetAsync(TagTarget.ForAlert(alertRow.Id), ct)).Items
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
        await Operations.Tags.UpsertAsync(TagTarget.ForEvent(taggedEvent.Id), new TagInput("retention"), ct: ct);

        // A wide retention window leaves recent events untouched.
        var keep = await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);
        Assert.Equal(0, keep.Events);
        Assert.NotEmpty(await Db.From<JobEvent>().Where(e => e.NamespaceId == ns).ToListAsync(ct));
        Assert.NotNull(await Operations.Tags.GetAsync(TagTarget.ForEvent(taggedEvent.Id), ct));

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
        await Operations.Tags.UpsertAsync(TagTarget.ForWorker(worker.Id), new TagInput("retention"), ct: ct);

        // Active workers are never purged, even with the cutoff in the future.
        var active = await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, -1, 1000, 50, ct);
        Assert.Equal(0, active.Workers);
        Assert.NotNull(await Db.From<JobWorker>().Where(w => w.Id == worker.Id).SingleOrDefaultAsync(ct));
        Assert.NotNull(await Operations.Tags.GetAsync(TagTarget.ForWorker(worker.Id), ct));

        // Retire it: age last_seen past a positive window, then the global sweep flips it to Dead.
        var agedAt = DateTime.UtcNow.AddHours(-1);
        await Db.From<JobWorker>().Where(w => w.Id == worker.Id).UpdateOnlyAsync(() => new JobWorker { LastHeartbeatAtUtc = agedAt }, ct);
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
        await Operations.Tags.UpsertAsync(TagTarget.ForAlert(settled.Id), new TagInput("retention", "delete"), ct: ct);
        await Operations.Tags.UpsertAsync(TagTarget.ForAlert(inFlight.Id), new TagInput("retention", "keep"), ct: ct);

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
            Assert.IsType<TagSet>(await Operations.Tags.GetAsync(TagTarget.ForAlert(inFlight.Id), ct)).Items
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
            await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);

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

    [Fact(DisplayName = "An expired terminal parent survives the sweep while a live child still references it")]
    public async Task Expired_terminal_parent_survives_while_a_child_is_alive()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var (parent, child) = await EnqueueParentAndChildAsync(ct);

        // Parent to terminal with zero retention; the child stays Ready (default 90-day window).
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);

        // The lineage guard keeps the expired parent until its descendant is deletable, so the
        // child's parent_id / lineage_root_id never dangle.
        Assert.NotNull(await Db.From<Job>().Where(j => j.Id == parent.JobId).SingleOrDefaultAsync(ct));
        Assert.NotNull(await Db.From<Job>().Where(j => j.Id == child.JobId).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "A fully expired subtree drains child-first and then releases the parent")]
    public async Task Fully_expired_subtree_drains_child_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var (parent, child) = await EnqueueParentAndChildAsync(ct);

        // Both terminal with zero retention: the whole subtree is immediately eligible.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(child, ct));

        // What this asserts is the lineage guarantee, not a per-call one: the sweep deletes leaves
        // only, so a parent is never removed while a child still references it, and a fully expired
        // subtree drains bottom-up. It deliberately does NOT assert that any single call deletes a
        // given row. PurgeExpiredData is a best-effort sweep: it selects WITH (UPDLOCK, READPAST),
        // so a contended row is skipped rather than waited for, and its iteration budget is bounded.
        // Progress and ordering are the contract; call count is not.
        var childGoneAfter = -1;
        var parentGoneAfter = -1;
        for (var pass = 1; pass <= 20 && (childGoneAfter < 0 || parentGoneAfter < 0); pass++)
        {
            await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);

            var childAlive = await Db.From<Job>().Where(j => j.Id == child.JobId).SingleOrDefaultAsync(ct) is not null;
            var parentAlive = await Db.From<Job>().Where(j => j.Id == parent.JobId).SingleOrDefaultAsync(ct) is not null;

            // The guarantee under test: the parent never outlives its child in the other direction.
            Assert.True(!(!parentAlive && childAlive), $"parent {parent.JobId} was purged while child {child.JobId} still existed");

            if (!childAlive && childGoneAfter < 0)
            {
                childGoneAfter = pass;
            }
            if (!parentAlive && parentGoneAfter < 0)
            {
                parentGoneAfter = pass;
            }
        }

        Assert.True(childGoneAfter > 0, $"child {child.JobId} never drained");
        Assert.True(parentGoneAfter > 0, $"expired parent {parent.JobId} never drained after its child was purged");
        Assert.True(childGoneAfter <= parentGoneAfter, "the subtree drained parent-first, which would orphan the child's lineage");
    }

    // A real parent/child pair through the enqueue path: the child is enqueued under the parent's id
    // (while the parent is still Ready), so lineage columns are stamped by the provider routine.
    private async Task<(JobEnqueueOutcome Parent, JobEnqueueOutcome Child)> EnqueueParentAndChildAsync(CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var json = serializers.Resolve(JobPayloadFormat.Json.Id);
        var parent = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "purge-now", json.Serialize(new PurgeProbe("parent")), null, null, null),
            ct
        );
        var child = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "purge-now",
                json.Serialize(new PurgeProbe("child")),
                null,
                null,
                null,
                ParentJobId: parent.JobId
            ),
            ct
        );
        return (parent, child);
    }

    [Fact(DisplayName = "The lock sweep is bounded by batch size and iterations like every other section")]
    public async Task Lock_sweep_is_bounded_by_batch_size_and_iterations()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var locks = Services.GetRequiredService<ILockStore>();

        // Three immediately-reapable rows (negative TTL) plus one live row. The reap is global, so a
        // concurrent spec's full purge may sweep some dead rows first; the call's OWN count is still
        // deterministically capped by batch x iterations, which is the property under test.
        string[] deadKeys = [TestKey("lock-batch.d1"), TestKey("lock-batch.d2"), TestKey("lock-batch.d3")];
        foreach (var key in deadKeys)
        {
            Assert.NotNull(await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(-1), ownerJobId: -1, ct));
        }
        var liveKey = TestKey("lock-batch.live");
        var liveToken = await locks.TryAcquireAsync(liveKey, TimeSpan.FromSeconds(3600), ownerJobId: -1, ct);
        Assert.NotNull(liveToken);

        try
        {
            var bounded = await RetentionTestOps.PurgeAsync(
                Services,
                ns,
                NoEventPurgeDays,
                NoAlertPurgeDays,
                NoWorkerPurgeSeconds,
                batchSize: 1,
                maxIterations: 1,
                ct
            );
            Assert.True(bounded.Locks <= 1, $"lock sweep deleted {bounded.Locks} rows under a 1x1 budget");

            // Full-budget runs clear this test's remaining dead rows; drain with bounded retries
            // because a concurrent spec's purge can hold rows this call's SKIP LOCKED sweep skipped.
            for (var attempt = 0; ; attempt++)
            {
                await RetentionTestOps.PurgeAsync(Services, ns, NoEventPurgeDays, NoAlertPurgeDays, NoWorkerPurgeSeconds, 1000, 50, ct);
                var remaining = new List<string>();
                foreach (var key in deadKeys)
                {
                    if (await Db.From<Lease>().Where(l => l.LeaseKey == key).SingleOrDefaultAsync(ct) is not null)
                    {
                        remaining.Add(key);
                    }
                }
                if (remaining.Count == 0)
                {
                    break;
                }
                Assert.True(attempt < 10, "expired locks never drained: " + string.Join(", ", remaining));
                await Task.Delay(100, ct);
            }
            Assert.NotNull(await Db.From<Lease>().Where(l => l.LeaseKey == liveKey).SingleOrDefaultAsync(ct));
        }
        finally
        {
            await locks.ReleaseAsync(liveToken!.Value, ct);
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
