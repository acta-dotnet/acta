using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>
/// Conformance for the <c>start_execution</c> version-CAS. A buffered claim whose row changed between
/// claim and start (reclaim/steal bumps <c>runtimes.version</c>) must not transition the row to Executing:
/// the start fails as <c>LostClaim</c>, leaving the job for the owner that actually holds the row.
/// </summary>
[ConformanceSpec(
    "start-execution.claim-cas",
    "Start execution honors the version CAS and the live-lease guard",
    Area = "Execution",
    Contract = "Start transitions a fresh claim to Executing but refuses a stale-version claim as LostClaim and an expired-lease claim as LeaseExpired."
)]
public sealed class StartExecutionStaleVersionSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A fresh matching claim starts execution and the job goes Executing")]
    public async Task Fresh_claim_version_starts_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        _ = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var claimed = Assert.Single(
            await Services.GetRequiredService<Acta.Runtime.Execution>().ClaimOneAsync(ns, workerId, leaseTtl, enqueued, ct)
        );

        var action = (
            await Services
                .GetRequiredService<Acta.Runtime.Execution>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.Started, action);
        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Executing, snapshot!.Status);
    }

    [Fact(DisplayName = "A stale-version claim is refused as LostClaim and the job stays Dispatched")]
    public async Task Stale_claim_version_is_refused_as_lost_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        _ = Services.GetRequiredService<ISqlDialect>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var claimed = Assert.Single(
            await Services.GetRequiredService<Acta.Runtime.Execution>().ClaimOneAsync(ns, workerId, leaseTtl, enqueued, ct)
        );

        // Same id / worker / execution_number as the live claim, but a version that no longer matches
        // the row - exactly the state a reclaim or steal leaves behind for the stale buffered claim.
        var action = (
            await Services
                .GetRequiredService<Acta.Runtime.Execution>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version + 1, leaseTtl, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.LostClaim, action);
        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Dispatched, snapshot!.Status);
    }

    [Fact(DisplayName = "An expired-lease claim is refused as LeaseExpired with no JobExecutionStarted event")]
    public async Task Expired_lease_is_refused_as_lease_expired()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        var dialect = Services.GetRequiredService<ISqlDialect>();

        // Claim with a negative TTL so the lease lands in the past - the worker that buffered this
        // claim past its lease, with the row not yet reclaimed (same id / worker / execution_number /
        // version, still Dispatched). The version CAS alone would let this start; the live-lease guard
        // refuses it.
        var claimed = Assert.Single(
            await Services.GetRequiredService<Acta.Runtime.Execution>().ClaimOneAsync(ns, workerId, leaseTtlSeconds: -5, enqueued, ct)
        );

        var action = (
            await Services
                .GetRequiredService<Acta.Runtime.Execution>()
                .StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtlSeconds: 180, ct)
        ).Action;

        Assert.Equal(StartExecutionAction.LeaseExpired, action);
        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Dispatched, snapshot!.Status);

        var startedEvents = await Db.From<EventEntity>()
            .Where(e => e.JobId == claimed.JobId && e.EventCode == EventCode.JobExecutionStarted)
            .ToListAsync(ct);
        Assert.Empty(startedEvents);
    }

    private static async Task<int> WorkerIdAsync(IDbSession session, int ns, CancellationToken ct)
    {
        var worker = await session.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!.Id;
    }
}
