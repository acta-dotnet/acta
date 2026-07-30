using Acta.Configuration;
using Acta.Modules.Execution;
using Acta.Modules.Execution.Workers;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for the always-on heartbeat (<c>extend_worker_leases</c>). Default lease TTL, so a
/// freshly-claimed lease is live: the heartbeat pushes it further out and stamps the worker's
/// <c>last_seen_at_utc</c>, and a direct <c>reclaim_stuck_jobs</c> sweep leaves the live lease alone.
/// </summary>
[ConformanceSpec(
    "extend-worker-leases.heartbeat",
    "Heartbeat extends a live lease and stamps last_seen",
    Area = "Execution",
    Contract = "The heartbeat pushes a live lease further out and advances the worker's last_seen without bumping the runtime version, and a reclaim sweep leaves it claimed.",
    Arrange = "A job is enqueued and claimed by a worker so a live lease exists at the default TTL.",
    Act = "The heartbeat runs ExtendWorkerLeases and a reclaim sweep is driven over the live lease.",
    Assert = "The lease is pushed further out with last_seen advanced and the runtime version unbumped, and the sweep leaves it claimed."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.ExtendWorkerLeasesAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartExecutionAsync))]
public abstract class ExtendWorkerLeasesSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "The heartbeat pushes a live lease further out, advances worker last_seen, and the lease survives a reclaim sweep")]
    public async Task Heartbeat_extends_a_live_lease_and_reclaim_leaves_it_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var workerBefore = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(workerBefore);
        var workerId = workerBefore!.Id;
        var lastSeenBefore = workerBefore.LastSeenAtUtc;

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var claim = await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, leaseTtl, enqueued, ct);
        var leaseAfterClaim = Assert.Single(claim).LeaseExpiresAtUtc;

        // lease_expires_at_utc is DATETIME2(3): claim and extend re-stamp it as now + the same TTL, so the
        // heartbeat only moves it forward if its now lands in a later millisecond. Ensure that gap so the
        // strictly-greater assertion below is deterministic under load (claim->extend can be sub-ms).
        await Task.Delay(5, ct);

        var extended = await Services.GetRequiredService<IWorkerStore>().ExtendWorkerLeasesAsync(workerId, leaseTtl, false, ct);
        Assert.Equal(enqueued.JobId, Assert.Single(extended));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Dispatched, job!.Status);
        Assert.NotNull(job.LeaseExpiresAtUtc);
        Assert.True(job.LeaseExpiresAtUtc > leaseAfterClaim, "heartbeat should push the lease further out");

        var workerAfter = await Db.From<JobWorker>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct);
        Assert.NotNull(workerAfter);
        Assert.True(workerAfter!.LastSeenAtUtc > lastSeenBefore, "heartbeat should advance last_seen_at_utc");

        // A live (heartbeat-fresh) lease is not stuck - reclaim finds nothing and leaves it claimed.
        Assert.Equal(0, (await Services.GetRequiredService<IExecutionStore>().ReclaimStuckJobsAsync(ns, ct)).Reclaimed);
        var still = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Dispatched, still!.Status);
        Assert.Equal(workerId, still.LeasedByWorkerId);
    }

    [Fact(DisplayName = "The heartbeat does not bump the runtime version, so a buffered claim still passes the start CAS and runs")]
    public async Task Buffered_claim_survives_a_heartbeat_then_starts()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        var workerId = worker!.Id;

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, leaseTtl, enqueued, ct)
        );

        // The worker's own heartbeat refreshes the lease while this claim is still buffered (Dispatched,
        // not yet started). It must not advance the runtime version, or the claim-time version below
        // fails the start CAS as LostClaim and the row stalls leased-but-never-run.
        var extended = await Services.GetRequiredService<IWorkerStore>().ExtendWorkerLeasesAsync(workerId, leaseTtl, false, ct);
        Assert.Equal(enqueued.JobId, Assert.Single(extended));

        var action = await Services
            .GetRequiredService<IExecutionStore>()
            .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct);

        Assert.Equal(StartExecutionAction.Started, action);
        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Executing, snapshot!.Status);
    }
}
