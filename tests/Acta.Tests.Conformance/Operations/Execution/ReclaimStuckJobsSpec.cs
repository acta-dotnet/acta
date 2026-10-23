using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for crash recovery's reclaim routine (<c>reclaim_stuck_jobs</c>). A claim with a
/// negative lease TTL stamps <c>lease_expires_at_utc</c> in the past - deterministically
/// simulating a worker that claimed a job then died long enough ago for its lease to lapse, with no
/// real-time wait. A stuck job (Executing + expired lease) returns to Ready on reclaim, or
/// goes terminal Failed once <c>failure_count</c> reaches the definition's MaxAttempts.
/// </summary>
[ConformanceSpec(
    "reclaim-stuck-jobs.recovery",
    "Reclaim returns an expired-lease job to Ready or fails it at MaxAttempts",
    Area = "Recovery",
    Contract = "An expired-lease job returns to Ready charged one failure, or lands terminal Failed once MaxAttempts is reached."
)]
public sealed class ReclaimStuckJobsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Job goes terminal Failed once failure_count reaches MaxAttempts")]
    public async Task Reclaim_fails_the_job_once_max_attempts_is_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var def = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == "add-numbers")
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        var maxAttempts = def!.MaxAttempts;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Each cycle loses a started attempt, then reclaims it. failure_count climbs by one per cycle; the
        // row stays Ready until the cycle that reaches MaxAttempts, where it goes terminal Failed.
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, workerId, enqueued.JobId, ct);

            Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

            var job = await ReadJobAsync(enqueued.JobId, ct);
            Assert.Equal(attempt, job.FailureCount);
            Assert.Equal(attempt < maxAttempts ? JobStatusCode.Ready : JobStatusCode.Failed, job.Status);

            // A terminal Failed landing must carry a retention deadline or purge never deletes it;
            // an in-budget re-arm leaves retention untouched.
            Assert.Equal(attempt < maxAttempts, job.RetentionUntilUtc is null);
        }
    }

    [Theory(DisplayName = "Reclaim saturates the failure count at 32,767: a recurring slot keeps re-arming and the count stops climbing")]
    [InlineData(32_766)]
    [InlineData(32_767)]
    public async Task Reclaim_saturates_the_failure_count(int staged)
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);

        // A lapsed attempt on a slot whose count already sits at or one below the ceiling.
        Assert.Equal(
            1,
            await Db.From<JobStateEntity>()
                .Where(r => r.Id == slotId)
                .UpdateOnlyAsync(
                    () =>
                        new JobStateEntity
                        {
                            Status = JobStatusCode.Executing,
                            FailureCount = staged,
                            LeasedByWorkerId = workerId,
                            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
                        },
                    ct
                )
        );

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal(32_767, slot.FailureCount);
        Assert.Equal(
            EventReason.LeaseExpired,
            EventDetail(await ReadSingleEventAsync(slotId, EventCode.JobExecutionOrphaned, ct), "reason")
        );
    }

    [Fact(DisplayName = "Expired EXECUTING lease is reclaimed as Orphaned, returning the job to Ready with failure_count bumped")]
    public async Task Executing_lease_expiry_is_reclaimed_and_returns_job_to_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Claim with a live lease; the claim lands the job Executing.
        const int LiveLeaseTtl = 30;
        var claimed = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );

        // Back-date the lease so the Executing row is eligible for reclaim.
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);

        var reclaimed = await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct);
        Assert.Equal(1, reclaimed.Reclaimed);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Equal(1, job.FailureCount);

        // The stale holder is the event's worker, and the details say why and where the job went.
        var reclaimEvent = await ReadSingleEventAsync(enqueued.JobId, EventCode.JobExecutionOrphaned, ct);
        Assert.Equal(workerId, reclaimEvent.WorkerId);
        Assert.Equal(claimed.ExecutionNumber, reclaimEvent.ExecutionNumber);
        Assert.Equal(EventReason.LeaseExpired, EventDetail(reclaimEvent, "reason"));
        Assert.Equal(JobStatusCode.Ready.Code, EventDetail(reclaimEvent, "to"));
    }

    [Fact(DisplayName = "Live EXECUTING lease is not reclaimed: the job stays Executing with no LeaseExpired event")]
    public async Task Live_executing_lease_is_not_reclaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Claim then start with a live (future) lease: the row lands Executing(50) with lease_expires_at_utc > now.
        const int LiveLeaseTtl = 30;
        var claimed = Assert.Single(
            await Services.GetRequiredService<ExecutionOperations>().ClaimJobAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );

        // One direct reclaim pass: must NOT touch the live-lease Executing row.
        var result = await Services.GetRequiredService<ExecutionOperations>().ReclaimStuckJobsAsync(ns, ct);
        Assert.Equal(0, result.Reclaimed);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.NotNull(job.LeasedByWorkerId);
        Assert.NotNull(job.LeaseExpiresAtUtc);

        Assert.Equal(0, await CountReasonAsync(enqueued.JobId, EventReason.LeaseExpired, ct));
    }

    [Fact(DisplayName = "A stale worker's sleep-timer consume leaves the reclaimed row's next run instant alone")]
    public async Task Sleep_timer_consume_does_not_clear_a_reclaimed_rows_next_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);
        var execution = Services.GetRequiredService<ExecutionOperations>();

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        const int LiveLeaseTtl = 30;
        const string TimerName = "t.stale-worker";
        var claimed = Assert.Single(await execution.ClaimJobAsync(ns, workerId, LiveLeaseTtl, enqueued, ct));

        var armed = await execution.ArmOrConsumeSleepTimerAsync(
            new ArmOrConsumeSleepTimerCommand(enqueued.JobId, TimerName, 600, null),
            ct
        );
        Assert.Equal(ArmOrConsumeSleepTimerOutcomeCode.Suspend, armed.Outcome);

        // Back-dating the arm's due instant is what puts the next call on the consume arm, which is the
        // one that clears next_run_at_utc; without it the call would simply re-suspend.
        Assert.Equal(
            1,
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.checkpoints SET due_at_utc = @p_due"
                    + " WHERE job_id = @p_job_id AND checkpoint_kind_code = @p_kind AND checkpoint_name = @p_name",
                ct,
                ("@p_due", DateTime.UtcNow.AddMinutes(-5)),
                ("@p_job_id", enqueued.JobId),
                ("@p_kind", (byte)CheckpointKindCode.Timer),
                ("@p_name", TimerName)
            )
        );

        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        var reclaimed = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.NotNull(reclaimed.NextRunAtUtc);

        // The reclaim owns the instant now. The stale worker's call still retires its own checkpoint,
        // but writing NULL over a Ready row would hide it from the claim's index order for good.
        var consumed = await execution.ArmOrConsumeSleepTimerAsync(
            new ArmOrConsumeSleepTimerCommand(enqueued.JobId, TimerName, 600, null),
            ct
        );
        Assert.Equal(ArmOrConsumeSleepTimerOutcomeCode.Continue, consumed.Outcome);

        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, after.Status);
        Assert.Equal(reclaimed.NextRunAtUtc, after.NextRunAtUtc);
    }

    private static async Task<int> WorkerIdAsync(IDbSession session, int ns, CancellationToken ct)
    {
        var worker = await session.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!.Id;
    }
}
