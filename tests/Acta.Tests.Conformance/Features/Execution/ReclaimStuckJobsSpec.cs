using Acta.Modules.Execution;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// Conformance for <c>sys.recovery</c>'s recovery routine (<c>reclaim_stuck_jobs</c>). LeaseTtlSeconds
/// is overridden negative so a claim stamps <c>lease_expires_at_utc</c> in the past - deterministically
/// simulating a worker that claimed a job then died long enough ago for its lease to lapse, with no
/// real-time wait. A stuck job (Dispatched|Executing + expired lease) returns to Ready on reclaim, or
/// goes terminal Failed once <c>failure_count</c> reaches the definition's MaxAttempts.
/// </summary>
[ConformanceSpec(
    "reclaim-stuck-jobs.recovery",
    "Reclaim returns an expired-lease job to Ready or fails it at MaxAttempts",
    Area = "Recovery",
    Contract = "An expired-lease job returns to Ready with failure_count incremented, or lands terminal Failed once MaxAttempts is reached.",
    Arrange = "An add-numbers job is enqueued and claimed with a negative lease TTL so its lease is already expired.",
    Act = "ReclaimStuckJobs sweeps the namespace after each claim cycle.",
    Assert = "The job returns to Ready with failure_count incremented until MaxAttempts is reached, where it lands terminal Failed."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ReclaimStuckJobsAsync))]
public abstract class ReclaimStuckJobsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Claim with a negative TTL so the lease lands in the past, deterministically simulating a worker
    // that claimed then died long enough ago for its lease to lapse. Passed straight to ClaimOneAsync,
    // never through JobsOptions - production options reject a non-positive lease (JobsOptionsValidator).
    private const int LeaseTtlSeconds = -5;

    [Fact(
        DisplayName = "Expired-lease job returns to Ready with lease cleared, failure_count bumped, and an Orphaned execution-finished event from the system actor"
    )]
    public async Task Reclaim_returns_a_stuck_job_to_ready_and_emits_the_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var claim = await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LeaseTtlSeconds, enqueued, ct);
        Assert.Equal(enqueued.JobId, Assert.Single(claim).JobId);

        var reclaimed = await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct);
        Assert.Equal(1, reclaimed.Reclaimed);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Equal((short)1, job.FailureCount);

        var events = await Db.From<JobEvent>()
            .Where(e => e.JobId == enqueued.JobId && e.EventCode == JobEventCode.JobExecutionFinished)
            .ToListAsync(ct);
        var reclaimEvent = Assert.Single(events);
        Assert.Equal(JobActorCode.Sys, reclaimEvent.ActorCode);
        Assert.Equal(ExecutionStatusCode.Orphaned, reclaimEvent.ExecutionStatus);
        Assert.Equal(JobStatusCode.Ready, reclaimEvent.ToStatus);
        Assert.Equal(JobEventReasonCode.JobLeaseExpired, reclaimEvent.ReasonCode);
    }

    [Fact(DisplayName = "Job goes terminal Failed once failure_count reaches MaxAttempts")]
    public async Task Reclaim_fails_the_job_once_max_attempts_is_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var def = await Db.From<JobDefinition>().Where(d => d.NamespaceId == ns && d.Name == "add-numbers").SingleOrDefaultAsync(ct);
        Assert.NotNull(def);
        var maxAttempts = def!.MaxAttempts;

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Each cycle: re-claim (lease lands expired via the negative TTL) then reclaim. failure_count
        // climbs by one per cycle; the row stays Ready until the cycle that reaches MaxAttempts, where
        // it goes terminal Failed.
        for (short attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var claim = await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LeaseTtlSeconds, enqueued, ct);
            Assert.Equal(enqueued.JobId, Assert.Single(claim).JobId);

            Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

            var job = await ReadJobAsync(enqueued.JobId, ct);
            Assert.Equal(attempt, job.FailureCount);
            Assert.Equal(attempt < maxAttempts ? JobStatusCode.Ready : JobStatusCode.Failed, job.Status);

            // A terminal Failed landing must carry a retention deadline or purge never deletes it;
            // an in-budget re-arm leaves retention untouched.
            Assert.Equal(attempt < maxAttempts, job.RetentionUntilUtc is null);
        }
    }

    [Fact(DisplayName = "Expired EXECUTING lease is reclaimed as Orphaned, returning the job to Ready with failure_count bumped")]
    public async Task Executing_lease_expiry_is_reclaimed_and_returns_job_to_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Claim with a live lease, then transition to Executing. The job lands Executing(50).
        const int LiveLeaseTtl = 30;
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        var started = await Services
            .GetRequiredService<IExecutionStore>()
            .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, LiveLeaseTtl, ct);
        Assert.Equal(StartExecutionAction.Started, started);

        // Back-date the lease so the Executing row is eligible for reclaim.
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);

        var reclaimed = await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct);
        Assert.Equal(1, reclaimed.Reclaimed);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.LeaseExpiresAtUtc);
        Assert.Equal((short)1, job.FailureCount);

        var events = await Db.From<JobEvent>()
            .Where(e => e.JobId == enqueued.JobId && e.EventCode == JobEventCode.JobExecutionFinished)
            .ToListAsync(ct);
        var reclaimEvent = Assert.Single(events);
        Assert.Equal(ExecutionStatusCode.Orphaned, reclaimEvent.ExecutionStatus);
        Assert.Equal(JobEventReasonCode.JobLeaseExpired, reclaimEvent.ReasonCode);
    }

    [Fact(DisplayName = "Live EXECUTING lease is not reclaimed: the job stays Executing with no LeaseExpired event")]
    public async Task Live_executing_lease_is_not_reclaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Claim then start with a live (future) lease: the row lands Executing(50) with lease_expires_at_utc > now.
        const int LiveLeaseTtl = 30;
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LiveLeaseTtl, enqueued, ct)
        );
        var started = await Services
            .GetRequiredService<IExecutionStore>()
            .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, LiveLeaseTtl, ct);
        Assert.Equal(StartExecutionAction.Started, started);

        // One direct reclaim pass: must NOT touch the live-lease Executing row.
        var result = await Services.GetRequiredService<IExecutionStore>().ReclaimStuckJobsAsync(ns, ct);
        Assert.Equal(0, result.Reclaimed);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, job.Status);
        Assert.Equal((short)0, job.FailureCount);
        Assert.NotNull(job.LeasedByWorkerId);
        Assert.NotNull(job.LeaseExpiresAtUtc);

        var leaseExpiredEvents = await Db.From<JobEvent>()
            .Where(e =>
                e.JobId == enqueued.JobId
                && e.EventCode == JobEventCode.JobExecutionFinished
                && e.ReasonCode == JobEventReasonCode.JobLeaseExpired
            )
            .ToListAsync(ct);
        Assert.Empty(leaseExpiredEvents);
    }

    private static async Task<int> WorkerIdAsync(IDbSession session, short ns, CancellationToken ct)
    {
        var worker = await session.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!.Id;
    }
}
