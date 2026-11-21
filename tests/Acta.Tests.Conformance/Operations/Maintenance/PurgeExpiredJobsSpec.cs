using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for <c>sys.retention</c>'s job delete (<c>PurgeExpiredJobs</c>): terminal jobs past
/// <c>retention_until_utc</c> go with what they own, and the guards keep a parent with children, a child of a parent not
/// yet due, a stopped recurring job of a live definition, and a job with an undelivered alert. A window wide enough to
/// exclude every event keeps the other deletes out of the facts; deletable terminal jobs are produced through the real
/// enqueue/execute/complete path via the zero-retention <c>purge-now</c> probe.
/// </summary>
[ConformanceSpec(
    "purge-expired-jobs.sweeps",
    "Purge deletes expired jobs with what they own, within batches",
    Area = "Retention",
    Contract = "Purge deletes terminal jobs past their retention with what they own, keeps the jobs its guards hold, and stays within its batch bounds."
)]
[Deploys("add-numbers", "purge-now")]
public sealed class PurgeExpiredJobsSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeExpiredJobsSpec>>
{
    // A window that makes the dated deletes a no-op for the rows produced in these tests: a huge positive
    // retention pushes the cutoff far into the past, so recent rows survive.
    private const int NoPurgeDays = 100_000;

    [Fact(DisplayName = "Job retention deletes the job's tags with the job")]
    public async Task Terminal_job_retention_cleans_the_job_tags()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var jobId = (await EnqueueAndRunAsync("purge-now", new PurgeProbe("x"), ct)).JobId;

        // Completed: the job is terminal Succeeded.
        Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));

        await Operations.Tags.UpsertAsync(TagTarget.ForJob(JobLookup.ById(jobId)), new TagInput("retention", "job"), ct: ct);
        // A step and a checkpoint: no key cascades them, so the delete has to name each table. They are written as a
        // handler writes them, under the execution fence of an attempt the job stands executing, then it is finished again.
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var fence = await ExecutionFenceStaging.ExecutingAsync(Db, jobId, ct);
        await execution.StartJobStepAsync(fence, "seed-step", atMostOnce: false, ct);
        await execution.AccessJobCheckpointAsync(
            new AccessJobCheckpointCommand(
                AccessJobCheckpointAction.Set,
                fence,
                CheckpointKindCode.Variable,
                "seed-variable",
                JobPayloadFormat.Json.Id,
                "1"u8.ToArray()
            ),
            ct
        );
        await JobStateStaging.SetStatusAsync(Db, jobId, JobStatusCode.Succeeded, ct);

        // Drain rather than assert a single call deletes it: one sweep can pass an eligible row over
        // (contended under READPAST, or stamped a rounded fraction of a millisecond into the future).
        // The count still has to come to exactly one across the whole drain, because only this job is
        // expired.
        var purged = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            NoPurgeDays,
            1000,
            50,
            async () => await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct) is null,
            ct
        );

        Assert.Equal(1, purged.Jobs);
        Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
        Assert.Empty(await Db.From<JobStateEntity>().Where(r => r.Id == jobId).ToListAsync(ct));
        Assert.Empty(await Db.From<StepEntity>().Where(s => s.JobId == jobId).ToListAsync(ct));
        Assert.Empty(await Db.From<CheckpointEntity>().Where(c => c.JobId == jobId).ToListAsync(ct));
        Assert.Empty((await Operations.Tags.ListAsync(new ListTagsQuery(TagTarget.ForJob(JobLookup.ById(jobId))), ct)).Items);
        Assert.Empty(await Db.From<TagEntity>().Where(t => t.EntityCode == EntityCode.Job && t.EntityId == jobId).ToListAsync(ct));
    }

    [Fact(DisplayName = "Retention keeps a job while an alert of it is undelivered, and takes its settled alerts with it")]
    public async Task A_job_with_an_undelivered_alert_stays_and_settled_alerts_go_with_their_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var waiting = (await EnqueueAndRunAsync("purge-now", new PurgeProbe("waiting"), ct)).JobId;
        var retrying = (await EnqueueAndRunAsync("purge-now", new PurgeProbe("retrying"), ct)).JobId;
        var told = (await EnqueueAndRunAsync("purge-now", new PurgeProbe("told"), ct)).JobId;
        await RaiseForJobAsync(waiting, AlertDeliveryStatusCode.Pending, key: null, ct);
        await RaiseForJobAsync(retrying, AlertDeliveryStatusCode.Retrying, key: null, ct);
        // Keyed, as every automatic alert is: on SQL Server its identity row is locked before the row.
        await RaiseForJobAsync(told, AlertDeliveryStatusCode.Delivered, Unique("told"), ct);

        // The retention window is not due, so the settled alert goes only because its job does.
        var purged = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            NoPurgeDays,
            1000,
            50,
            async () => await Db.From<JobEntity>().Where(j => j.Id == told).SingleOrDefaultAsync(ct) is null,
            ct
        );

        // The failure an undelivered alert reports has reached nobody yet, and delivering it names the job.
        Assert.Equal(1, purged.Jobs);
        foreach (var kept in new[] { waiting, retrying })
        {
            Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == kept).SingleOrDefaultAsync(ct));
            Assert.Single(await Db.From<AlertEntity>().Where(a => a.JobId == kept).ToListAsync(ct));
        }

        Assert.Empty(await Db.From<AlertEntity>().Where(a => a.JobId == told).ToListAsync(ct));
    }

    private Task RaiseForJobAsync(long jobId, AlertDeliveryStatusCode delivery, string? key, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "retention",
            message: "retention",
            channelName: "default",
            delivery,
            deduplicationKey: key,
            ct
        );

    [Fact(DisplayName = "A future-retention job stamped with the default window survives the purge")]
    public async Task Future_retention_job_survives_and_is_stamped_with_the_default_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // retention_until_utc is stamped by the database at completion, so bracket it with two DB-clock
        // reads rather than DateTime.UtcNow: the test process's clock can skew from the container's.
        var clock = Services.GetRequiredService<IActaClock>();
        var before = await clock.GetUtcNowAsync(ct);
        var jobId = (await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct)).JobId;
        var after = await clock.GetUtcNowAsync(ct);

        // add-numbers carries no [Job(JobRetention=...)], so completion stamps JobsOptions.Retention, a year by default.
        var job = await ReadJobAsync(jobId, ct);
        Assert.NotNull(job!.RetentionUntilUtc);
        Assert.InRange(job.RetentionUntilUtc!.Value, before.AddDays(365).AddSeconds(-1), after.AddDays(365).AddSeconds(1));

        var result = await RetentionTestOps.PurgeAsync(Services, ns, NoPurgeDays, 1000, 50, ct);

        Assert.Equal(0, result.Jobs);
        Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == jobId).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "An expired terminal parent survives the sweep while a live child still references it")]
    public async Task Expired_terminal_parent_survives_while_a_child_is_alive()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var (parent, child) = await EnqueueParentAndChildAsync(ct);

        // Parent to terminal with zero retention; the child stays Ready.
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));

        await RetentionTestOps.PurgeAsync(Services, ns, NoPurgeDays, 1000, 50, ct);

        // The lineage guard keeps the expired parent until its descendant is deletable, so the
        // child's parent_job_id / lineage_root_job_id never dangle.
        Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == parent.JobId).SingleOrDefaultAsync(ct));
        Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == child.JobId).SingleOrDefaultAsync(ct));
    }

    [Fact(DisplayName = "A fully expired subtree drains child-first and then releases the parent")]
    public async Task Fully_expired_subtree_drains_child_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var (parent, child) = await EnqueueParentAndChildAsync(ct);

        // Both terminal with zero retention: the whole subtree is immediately eligible.
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(child, ct));

        // What this asserts is the lineage guarantee, not a per-call one: the sweep deletes leaves
        // only, so a parent is never removed while a child still references it, and a fully expired
        // subtree drains bottom-up. It deliberately does NOT assert that any single call deletes a
        // given row. PurgeExpiredJobs is a best-effort sweep: it selects WITH (UPDLOCK, READPAST),
        // so a contended row is skipped rather than waited for, and its iteration budget is bounded.
        // Progress and ordering are the contract; call count is not.
        var childGoneAfter = -1;
        var parentGoneAfter = -1;
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        for (var pass = 1; DateTime.UtcNow < deadline && (childGoneAfter < 0 || parentGoneAfter < 0); pass++)
        {
            await RetentionTestOps.PurgeAsync(Services, ns, NoPurgeDays, 1000, 50, ct);

            var childAlive = await Db.From<JobEntity>().Where(j => j.Id == child.JobId).SingleOrDefaultAsync(ct) is not null;
            var parentAlive = await Db.From<JobEntity>().Where(j => j.Id == parent.JobId).SingleOrDefaultAsync(ct) is not null;

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
            if (childGoneAfter < 0 || parentGoneAfter < 0)
            {
                await Task.Delay(25, ct);
            }
        }

        Assert.True(childGoneAfter > 0, $"child {child.JobId} never drained");
        Assert.True(parentGoneAfter > 0, $"expired parent {parent.JobId} never drained after its child was purged");
        Assert.True(childGoneAfter <= parentGoneAfter, "the subtree drained parent-first, which would orphan the child's lineage");
    }

    // A real parent/child pair through the enqueue path: the child is enqueued under the parent's id
    // (while the parent is still Ready), so lineage columns are stamped by the provider routine.
    private async Task<(EnqueueJobResponse Parent, EnqueueJobResponse Child)> EnqueueParentAndChildAsync(CancellationToken ct)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var json = serializers.Resolve(JobPayloadFormat.Json.Id);
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "purge-now", json.Serialize(new PurgeProbe("parent")), null, null, null),
            ct
        );
        var child = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
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

    [Fact(DisplayName = "Batching caps a single call at max iterations and a full run clears the rest")]
    public async Task Batching_caps_a_single_call_at_max_iterations_and_a_full_run_clears_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var a = await EnqueueAndRunAsync("purge-now", new PurgeProbe("a"), ct);
        var b = await EnqueueAndRunAsync("purge-now", new PurgeProbe("b"), ct);

        // batch 1 x 1 iteration never takes more than one of the two due jobs. Repeated until one goes,
        // because a pass is allowed to take none: a row an unrelated transaction holds is skipped under
        // READPAST, and a job completed microseconds ago is stamped with a retention_until_utc rounded
        // into the next millisecond, which the sweep's own clock has not reached yet. Neither weakens the
        // cap: if a 1x1 budget could ever take both, the drained total would be two.
        var first = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            NoPurgeDays,
            batchSize: 1,
            maxIterations: 1,
            async () => (await Db.From<JobEntity>().Where(j => j.Id == a.JobId || j.Id == b.JobId).ToListAsync(ct)).Count == 1,
            ct
        );
        Assert.Equal(1, first.Jobs);

        // A full-budget run clears the remainder.
        var rest = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            NoPurgeDays,
            batchSize: 1000,
            maxIterations: 50,
            async () => (await Db.From<JobEntity>().Where(j => j.Id == a.JobId || j.Id == b.JobId).ToListAsync(ct)).Count == 0,
            ct
        );
        Assert.Equal(1, rest.Jobs);
    }

    [Fact(DisplayName = "A stopped recurring job outlives its retention while its definition is live, and drains once retired")]
    public async Task A_stopped_recurring_job_outlives_retention_until_its_definition_is_retired()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var jobName = $"purge-recurring-{Guid.NewGuid():N}";
        var defId = (await DefinitionTestOps.RegisterAsync(Services, ns, [Def(jobName)], ct))[jobName];
        var cursor = DateTime.UtcNow.AddHours(6);
        var slots = await ScheduleTestOps.RegisterAsync(
            Services,
            [
                new DefinitionSchedules(
                    NamespaceId: ns,
                    DefinitionId: defId,
                    InputFormatId: 0,
                    Input: ReadOnlyMemory<byte>.Empty,
                    ScheduledDelivery: ScheduledDeliveryCode.Permanent,
                    Schedules:
                    [
                        new SlotSchedule(
                            "only",
                            "0 * * * *",
                            null,
                            MisfireStrategyCode.Skip,
                            ScheduleExpressionKindCode.Cron,
                            null,
                            cursor
                        ),
                    ]
                ),
            ],
            ct
        );
        var slotId = slots[defId];

        // An operator cancelled it a retention window ago.
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, next_run_at_utc = NULL, retention_until_utc = @p_until WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Cancelled),
            ("@p_until", DateTime.UtcNow.AddDays(-1)),
            ("@p_id", slotId)
        );

        await RetentionTestOps.PurgeAsync(Services, ns, NoPurgeDays, 1000, 50, ct);
        Assert.NotNull(await Db.From<JobEntity>().Where(j => j.Id == slotId).SingleOrDefaultAsync(ct));
        Assert.NotEmpty(await Db.From<ScheduleEntity>().Where(x => x.JobId == slotId).ToListAsync(ct));

        await Db.ExecuteRawAsync(
            "UPDATE {schema}.definitions SET definition_status_code = @p_status WHERE definition_id = @p_id",
            ct,
            ("@p_status", (byte)DefinitionStatusCode.Retired),
            ("@p_id", defId)
        );
        var purged = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            NoPurgeDays,
            1000,
            50,
            async () => await Db.From<JobEntity>().Where(j => j.Id == slotId).SingleOrDefaultAsync(ct) is null,
            ct
        );
        Assert.True(purged.Jobs >= 1);
        Assert.Null(await Db.From<JobEntity>().Where(j => j.Id == slotId).SingleOrDefaultAsync(ct));
    }

    private static JobDescriptor Def(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            ResultPayloadFormat: null,
            Priority: JobPriorityCode.Bulk,
            MaxAttempts: 1,
            EventRecording: EventRecordingCode.Failures,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
        );
}
