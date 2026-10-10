using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// A claim that committed and lost its answer: the row is leased by this worker and nothing in the process holds
/// it. The heartbeat renews only what the process holds, so the lease lapses and recovery returns the row to Ready,
/// uncharged when it was Dispatched and charged like a crash after start when the claim started it.
/// </summary>
[ConformanceSpec(
    "runtime.orphaned-claim",
    "A claim whose answer was lost lapses to recovery",
    Area = "Runtime",
    Contract = "A row leased by this worker that no attempt or buffered claim holds is not renewed by the heartbeat, and recovery returns it to Ready once its lease lapses."
)]
public sealed class OrphanedClaimChaosSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(DisplayName = "A Dispatched row whose claim answer was lost is not renewed, is reclaimed uncharged, and then runs once")]
    public async Task Lost_claim_answer_on_a_dispatched_row_lapses_uncharged()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The claim commits; the answer never reaches the worker. The row is Dispatched under this
        // worker's lease and nothing in the process knows about it.
        _faults.ThrowAfterClaimOnce();
        await Assert.ThrowsAsync<InjectedProviderError>(() => Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Dispatched, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);

        await LapsesToRecoveryAsync(enqueued.JobId, expectedFailures: 0, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
    }

    [Fact(
        DisplayName = "An Executing row whose combined claim answer was lost is not renewed and is reclaimed charged, like a crash after start"
    )]
    public async Task Lost_claim_answer_on_an_executing_row_lapses_charged()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);
        var namespaceId = await ChaosSpecHelpers.NamespaceIdAsync(Db, TestNamespace, ct);
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, namespaceId, ct);

        // The Direct profile starts the execution inside the claim, so a lost answer leaves the row
        // Executing rather than Dispatched. Staged straight against the store under the runtime's own
        // worker id, so the runtime holds the lease and knows nothing about it.
        var claim = await Services
            .GetRequiredService<IExecutionOperations>()
            .ClaimJobsAsync(new ClaimOptions(namespaceId, workerId, MaxBatch: 1, StartExecuting: true), 60, ct);
        Assert.Single(claim.Jobs);
        Assert.Equal(JobStatusCode.Executing, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);

        await LapsesToRecoveryAsync(enqueued.JobId, expectedFailures: 1, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
    }

    // A heartbeat leaves the lease where the claim put it; once it lapses, recovery returns the row to Ready.
    private async Task LapsesToRecoveryAsync(long jobId, int expectedFailures, CancellationToken ct)
    {
        var claimedLease = (await ReadJobAsync(jobId, ct)).LeaseExpiresAtUtc;
        await Task.Delay(5, ct);
        await Runtime.RunHeartbeatOnceAsync(ct);
        Assert.Equal(claimedLease, (await ReadJobAsync(jobId, ct)).LeaseExpiresAtUtc);

        await ChaosSpecHelpers.ExpireLeaseAsync(Db, jobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, Runtime.RegisteredNamespaceIds[TestNamespace], ct));
        var reclaimed = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Null(reclaimed.LeasedByWorkerId);
        Assert.Equal(expectedFailures, reclaimed.FailureCount);
    }
}
