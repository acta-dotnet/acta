using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Workers;

/// <summary>
/// Conformance for the always-on heartbeat (<c>extend_worker_leases</c>). Default lease TTL, so a
/// freshly-claimed lease is live: the heartbeat pushes a held lease further out and stamps the worker's
/// <c>last_seen_at_utc</c>, and a direct <c>reclaim_stuck_jobs</c> sweep leaves the live lease alone. A
/// lease the process does not hold is not renewed and lapses to recovery.
/// </summary>
[ConformanceSpec(
    "extend-worker-leases.heartbeat",
    "Heartbeat extends a held lease and stamps last_seen",
    Area = "Execution",
    Contract = "The heartbeat extends a held lease without a version bump and stamps last_seen, while a leased row the process does not hold lapses to recovery."
)]
public sealed class ExtendWorkerLeasesSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "The heartbeat pushes a held lease further out, advances worker last_seen, and the lease survives a reclaim sweep")]
    public async Task Heartbeat_extends_a_live_lease_and_reclaim_leaves_it_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var workerBefore = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(workerBefore);
        var workerId = workerBefore!.Id;
        var lastSeenBefore = workerBefore.LastHeartbeatAtUtc;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var claim = await Services.GetRequiredService<Acta.Execution>().ClaimOneAsync(ns, workerId, leaseTtl, enqueued, ct);
        var claimedRow = Assert.Single(claim);
        var leaseAfterClaim = claimedRow.LeaseExpiresAtUtc;

        // lease_expires_at_utc is DATETIME2(3): claim and extend re-stamp it as now + the same TTL, so the
        // heartbeat only moves it forward if its now lands in a later millisecond. Ensure that gap so the
        // strictly-greater assertion below is deterministic under load (claim->extend can be sub-ms).
        await Task.Delay(5, ct);

        var answer = await Services
            .GetRequiredService<WorkerLifecycle>()
            .ExtendWorkerLeasesAsync(workerId, leaseTtl, false, [(enqueued.JobId, claimedRow.ExecutionNumber)], ct);
        Assert.Equal(new ExtendWorkerLeasesLease(enqueued.JobId, claimedRow.ExecutionNumber, Renewed: true), Assert.Single(answer.Leases));
        var definitions = await Db.From<DefinitionEntity>().Where(d => d.NamespaceId == ns).ToListAsync(ct);
        Assert.Equal(definitions.Max(d => d.ModifiedAtUtc), answer.DefinitionsModifiedAtUtc);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Dispatched, job!.Status);
        Assert.NotNull(job.LeaseExpiresAtUtc);
        Assert.True(job.LeaseExpiresAtUtc > leaseAfterClaim, "heartbeat should push the lease further out");

        var workerAfter = await Db.From<WorkerEntity>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct);
        Assert.NotNull(workerAfter);
        Assert.True(workerAfter!.LastHeartbeatAtUtc > lastSeenBefore, "heartbeat should advance last_seen_at_utc");

        // A live (heartbeat-fresh) lease is not stuck - reclaim finds nothing and leaves it claimed.
        Assert.Equal(0, (await Services.GetRequiredService<Acta.Execution>().ReclaimStuckJobsAsync(ns, ct)).Reclaimed);
        var still = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Dispatched, still!.Status);
        Assert.Equal(workerId, still.LeasedByWorkerId);
    }

    [Fact(DisplayName = "The heartbeat does not bump the runtime version, so a buffered claim still passes the start CAS and runs")]
    public async Task Buffered_claim_survives_a_heartbeat_then_starts()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        var workerId = worker!.Id;

        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var claimed = Assert.Single(
            await Services.GetRequiredService<Acta.Execution>().ClaimOneAsync(ns, workerId, leaseTtl, enqueued, ct)
        );

        // The worker's own heartbeat refreshes the lease while this claim is still buffered (Dispatched,
        // not yet started). It must not advance the runtime version, or the claim-time version below
        // fails the start CAS as LostClaim and the row stalls leased-but-never-run.
        var extended = (
            await Services
                .GetRequiredService<WorkerLifecycle>()
                .ExtendWorkerLeasesAsync(workerId, leaseTtl, false, [(enqueued.JobId, claimed.ExecutionNumber)], ct)
        ).Leases;
        Assert.Equal(new ExtendWorkerLeasesLease(enqueued.JobId, claimed.ExecutionNumber, Renewed: true), Assert.Single(extended));

        var action = (
            await Services
                .GetRequiredService<Acta.Execution>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.Started, action);
        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Executing, snapshot!.Status);
    }

    [Fact(
        DisplayName = "A leased row the process does not hold is neither renewed nor answered, and recovery reclaims it once its lease lapses"
    )]
    public async Task A_leased_row_the_process_does_not_hold_lapses_to_recovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var execution = Services.GetRequiredService<Acta.Execution>();
        var held = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);
        var lost = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);
        var heldClaim = Assert.Single(await execution.ClaimOneAsync(ns, workerId, leaseTtl, held, ct));
        var lostClaim = Assert.Single(await execution.ClaimOneAsync(ns, workerId, leaseTtl, lost, ct));
        var lostLease = (await ReadJobAsync(lost.JobId, ct)).LeaseExpiresAtUtc;
        await Task.Delay(5, ct);

        // The process holds the first row at its execution, beside an older execution of the same job, and the
        // second only at an execution the row has moved past: an attempt still unwinding after its job was claimed again.
        var extended = (
            await Services
                .GetRequiredService<WorkerLifecycle>()
                .ExtendWorkerLeasesAsync(
                    workerId,
                    leaseTtl,
                    false,
                    [
                        (held.JobId, heldClaim.ExecutionNumber - 1),
                        (held.JobId, heldClaim.ExecutionNumber),
                        (lost.JobId, lostClaim.ExecutionNumber - 1),
                    ],
                    ct
                )
        ).Leases;

        Assert.Equal(new ExtendWorkerLeasesLease(held.JobId, heldClaim.ExecutionNumber, Renewed: true), Assert.Single(extended));
        Assert.Equal(lostLease, (await ReadJobAsync(lost.JobId, ct)).LeaseExpiresAtUtc);

        await ChaosSpecHelpers.ExpireLeaseAsync(Db, lost.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        var reclaimed = await ReadJobAsync(lost.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Null(reclaimed.LeasedByWorkerId);
        Assert.Equal(0, reclaimed.FailureCount);
        Assert.Equal(JobStatusCode.Dispatched, (await ReadJobAsync(held.JobId, ct)).Status);
    }
}
