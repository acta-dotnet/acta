using Acta.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

[ConformanceSpec(
    "chaos.worker-crash-recovery",
    "Worker crash boundaries recover through one legal final state",
    Area = "Chaos",
    Contract = "A crash after claim, after start, after handler completion, or during a running handler is recovered by lease reclaim and has a single legal final state.",
    Arrange = "Store fault injection is armed to crash a worker at the claim, start, post-complete, and mid-handler boundaries.",
    Act = "A worker crashes at each boundary, its lease is expired, and reclaim orphans the attempt for a later worker.",
    Assert = "Every boundary recovers through a single legal final state and the job completes exactly once."
)]
public abstract class WorkerCrashRecoveryChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // A normal positive lease; chaos expires it explicitly via ExpireLeaseAsync, never by waiting.
    private const int LeaseTtlSeconds = 60;

    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(DisplayName = "Claim-only crash recovers with no execution-started event and an orphaned recovery event")]
    public async Task Worker_crashes_after_claim_before_StartExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. Claim the row, then crash before StartExecution by expiring the lease.
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker, LeaseTtlSeconds, enqueued, ct)
        );
        Assert.Equal(enqueued.JobId, claimed.JobId);
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);

        // --- 2. Reclaim returns the job to Ready, bumping failure_count, with no execution-started event.
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        var reclaimed = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(reclaimed);
        Assert.Equal(JobStatusCode.Ready, reclaimed!.Status);
        Assert.Equal((short)1, reclaimed.FailureCount);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.DoesNotContain(events, e => e.JobEventCode == JobEventCode.JobExecutionStarted);
        ChaosSpecHelpers.AssertRecoveryEvent(events, JobStatusCode.Dispatched, JobStatusCode.Ready);

        // --- 3. A later tick completes the job exactly once.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Done, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "Crash after start orphans the started execution before retry and finishes once")]
    public async Task Worker_crashes_after_StartExecution_before_handler_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. Claim and start, then crash before the handler completes by expiring the lease.
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker, LeaseTtlSeconds, enqueued, ct)
        );
        Assert.Equal(
            StartExecutionAction.Started,
            await Services
                .GetRequiredService<IExecutionStore>()
                .StartExecutionAsync(enqueued.JobId, worker, claimed.ExecutionNumber, claimed.Version, LeaseTtlSeconds, ct)
        );
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);

        // --- 2. Reclaim orphans the started execution before retry.
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.Single(events.Where(e => e.JobEventCode == JobEventCode.JobExecutionStarted));
        ChaosSpecHelpers.AssertRecoveryEvent(events, JobStatusCode.Executing, JobStatusCode.Ready);

        // --- 3. A later tick finishes the job with a single Succeeded execution.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var finished = (await GetEventsByJobId.Run(Services, enqueued.JobId, ct))
            .Where(e => e.JobEventCode == JobEventCode.JobExecutionFinished && e.ExecutionStatus == ExecutionStatusCode.Succeeded)
            .ToList();
        Assert.Single(finished);
        Assert.Equal(JobStatusCode.Done, await Jobs.GetStatusAsync(enqueued, ct));
    }

    [Fact(DisplayName = "Crash before CompleteExecution does not replay the durable step on recovery")]
    public async Task Worker_crashes_after_handler_completes_before_CompleteExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-step-before-complete", ct);

        // --- 1. The handler runs its durable step, then CompleteExecution fails before commit.
        _faults.ThrowBeforeCompleteOnce();
        await Assert.ThrowsAsync<TimeoutException>(() => Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        Assert.Equal(JobStatusCode.Executing, await Jobs.GetStatusAsync(enqueued, ct));

        // --- 2. Reclaim and rerun; the durable step is not replayed.
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);

        // --- 3. One orphaned recovery event and one Succeeded finish.
        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        ChaosSpecHelpers.AssertRecoveryEvent(events, JobStatusCode.Executing, JobStatusCode.Ready);
        ChaosSpecHelpers.AssertSingleFinished(events, ExecutionStatusCode.Succeeded, JobStatusCode.Executing, JobStatusCode.Done);
    }

    [Fact(DisplayName = "Lease expiry mid-handler cancels the lost lease and a fresh run completes the job once")]
    public async Task Lease_expires_while_handler_is_still_running()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-blocking", ct);
        ChaosProbes.Reset(enqueued.JobId);

        // --- 1. Start the handler and let it block, then expire its lease mid-run and reclaim.
        var run = Runtime.RunOnceAsync(enqueued, ct);
        await ChaosProbes.WaitStartedAsync(enqueued.JobId, ct);
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));

        // --- 2. Heartbeat cancels the lost lease; release the probe if cancellation is slow.
        await Runtime.RunHeartbeatOnceAsync(ct);
        var cancelled = ChaosProbes.WaitCancelledAsync(enqueued.JobId, ct);
        if (await Task.WhenAny(cancelled, Task.Delay(TimeSpan.FromSeconds(5), ct)) != cancelled)
        {
            ChaosProbes.Release(enqueued.JobId);
        }

        var stolenOutcome = await run;
        Assert.Contains(stolenOutcome, new[] { RunOnceOutcome.NothingClaimed, RunOnceOutcome.Rearmed });
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(enqueued, ct));

        // --- 3. A fresh run completes the job exactly once. The recovered attempt re-armed with a
        // retry backoff (next_run in the future), so force the row due before the final claim; this
        // spec verifies recovery + single completion, not the backoff interval.
        ChaosProbes.Reset(enqueued.JobId);
        ChaosProbes.Release(enqueued.JobId);
        await ChaosSpecHelpers.SetReadyAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Done, await Jobs.GetStatusAsync(enqueued, ct));

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        ChaosSpecHelpers.AssertRecoveryEvent(events, JobStatusCode.Executing, JobStatusCode.Ready);
        ChaosSpecHelpers.AssertSingleFinished(events, ExecutionStatusCode.Succeeded, JobStatusCode.Executing, JobStatusCode.Done);
    }
}
