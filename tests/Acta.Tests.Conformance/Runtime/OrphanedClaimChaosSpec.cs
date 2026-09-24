using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// A claim that committed and lost its answer: the rows are leased by this worker, the heartbeat renews
/// them from database state, and no attempt exists to start, run, or release them. The heartbeat
/// notices a renewed row that nothing in the process accounts for, waits one more tick so a claim still
/// on its way to an executor is never mistaken for one, and returns the row to Ready through the same
/// walk an unsupported claim takes.
/// </summary>
[ConformanceSpec(
    "runtime.orphaned-claim",
    "A claim whose answer was lost is returned to Ready by the heartbeat",
    Area = "Runtime",
    Contract = "A row leased by this worker with no attempt behind it for two heartbeats is re-armed Ready, and a row an attempt or the buffer holds is left alone.",
    Arrange = "A counting one-shot claimed by the runtime, with the claim's answer lost after the store committed it.",
    Act = "The heartbeat ticks twice with nothing running, then the job is run once.",
    Assert = "The row is Ready and unleased after the second tick, and the attempt then runs once to Succeeded."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimOneAsync))]
public abstract class OrphanedClaimChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(DisplayName = "A Dispatched row whose claim answer was lost is returned to Ready after two heartbeats and then runs once")]
    public async Task Lost_claim_answer_on_a_dispatched_row_is_released()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The claim commits; the answer never reaches the worker. The row is Dispatched under this
        // worker's lease and nothing in the process knows about it.
        _faults.ThrowAfterClaimOnce();
        await Assert.ThrowsAsync<InjectedProviderError>(() => Runtime.RunOnceAsync(enqueued, ct));
        var leased = await Jobs.GetAsync(JobLookup.ById(enqueued.JobId), ct);
        Assert.NotNull(leased);
        Assert.Equal(JobStatusCode.Dispatched, leased!.Status);
        Assert.NotNull(leased.LeasedByWorkerId);

        await ReleasedByTwoHeartbeatsAsync(enqueued.JobId, ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "An Executing row whose combined claim answer was lost is returned to Ready after two heartbeats")]
    public async Task Lost_claim_answer_on_an_executing_row_is_released()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);
        await Runtime.InitializeAsync(ct);
        var namespaceId = await ChaosSpecHelpers.NamespaceIdAsync(Db, TestNamespace, ct);
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, namespaceId, ct);

        // The Direct and Bulk profiles start the execution inside the claim, so a lost answer leaves
        // the row Executing rather than Dispatched. Staged straight against the store under the
        // runtime's own worker id, so the runtime holds the lease and knows nothing about it.
        var claim = await Services
            .GetRequiredService<IExecutionStore>()
            .ClaimBatchAsync(new ClaimRequest(namespaceId, workerId, MaxBatch: 1, StartExecuting: true), 60, ct);
        Assert.Single(claim.Jobs);
        Assert.Equal(JobStatusCode.Executing, await Jobs.GetStatusAsync(enqueued, ct));

        await ReleasedByTwoHeartbeatsAsync(enqueued.JobId, ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
    }

    // The first tick only notes the row; the second confirms it and hands it to the executor, which
    // releases it on its own task, so the row is polled until it reads Ready and unleased.
    private async Task ReleasedByTwoHeartbeatsAsync(long jobId, CancellationToken ct)
    {
        await Runtime.RunHeartbeatOnceAsync(ct);
        var afterOne = await Jobs.GetAsync(JobLookup.ById(jobId), ct);
        Assert.NotNull(afterOne!.LeasedByWorkerId);

        await Runtime.RunHeartbeatOnceAsync(ct);
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        JobDetail? row;
        do
        {
            row = await Jobs.GetAsync(JobLookup.ById(jobId), ct);
            if (row is { Status: JobStatusCode.Ready, LeasedByWorkerId: null })
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        } while (DateTime.UtcNow < deadline);

        Assert.Fail($"The row was not released: status {row?.Status}, leased by {row?.LeasedByWorkerId}.");
    }
}
