using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The window between a claim and its handler: the start write, and the reads the handler needs. A
/// claimed row is this worker's, and the heartbeat renews every row it holds from database state, so
/// nothing that goes wrong in this window may end with the row leased and nothing left to progress it.
/// The start is reconciled against the row rather than trusted from one answer, because an operator
/// verb can move the version under a claim and a committed start can lose its answer; the reads are
/// repeated the way the writes are.
/// </summary>
[ConformanceSpec(
    "runtime.start-reconciliation",
    "A claim survives what happens between its claim and its handler",
    Area = "Runtime",
    Contract = "A start is reconciled against the row on LostClaim, a committed start that lost its answer is not made twice, and a failing setup read is repeated.",
    Arrange = "A counting one-shot and a recurring slot, with faults staged between the claim and the start write and on the database clock read.",
    Act = "The row is reprioritized between claim and start, a start commits and loses its answer, and a recurring fire's clock read fails once.",
    Assert = "Each attempt runs once to Succeeded with one started event, and the recurring fire completes with its slot Ready and unleased."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.StartExecutionAsync))]
public abstract class StartReconciliationChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(
        DisplayName = "A reprioritize between the claim and the start does not strand the job: the start is retried against the moved version"
    )]
    public async Task Reprioritize_between_claim_and_start_is_absorbed()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The verb bumps runtimes.version on the Dispatched row without touching ownership, so the start
        // written against the claim's version answers LostClaim while the lease is still this worker's.
        _faults.RunBeforeStartOnce(async () => await Jobs.ReprioritizeAsync(enqueued, JobPriorityCode.High, ct: ct));

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
        Assert.Single(await GetEventsByJobId.Run(Services, enqueued.JobId, ct), e => e.EventCode == EventCode.JobExecutionStarted);
    }

    [Fact(
        DisplayName = "A start that fails before it commits, with a reprioritize in its retry window, is retried against the moved version"
    )]
    public async Task Failed_start_then_reprioritize_then_retry_is_absorbed()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The first start never reaches the store and the version moves while the repeat waits, so the
        // retry answers LostClaim on a row that is still this worker's and still Dispatched.
        _faults.RunBeforeStartOnce(async () =>
        {
            await Jobs.ReprioritizeAsync(enqueued, JobPriorityCode.High, ct: ct);
            throw new InjectedProviderError("before StartExecution");
        });

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
        Assert.Single(await GetEventsByJobId.Run(Services, enqueued.JobId, ct), e => e.EventCode == EventCode.JobExecutionStarted);
    }

    [Fact(DisplayName = "A start that commits and loses its answer runs the handler once and writes one started event")]
    public async Task Start_that_commits_and_loses_its_answer_is_not_made_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The repeat resubmits the start against the claim's version; the row is already Executing at
        // the next version, so the answer is LostClaim and the row itself says the attempt is this
        // worker's to run.
        _faults.ThrowAfterStartOnce();

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
        Assert.Single(await GetEventsByJobId.Run(Services, enqueued.JobId, ct), e => e.EventCode == EventCode.JobExecutionStarted);
    }

    [Fact(DisplayName = "A clock read that fails once after the claim is repeated, and the recurring fire completes")]
    public async Task Failing_setup_read_is_repeated()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);

        // The clock is read after the claim and before the handler; a fault there used to escape to the
        // worker loop with the slot still Dispatched under a lease the heartbeat kept renewing.
        _faults.ThrowGetUtcNowOnce();
        await AlertTestOps.FireSlotUntilAsync(
            Services,
            Runtime,
            slotId,
            () => RecurringPingHandler.TriggersFor(TestNamespace).Count,
            1,
            ct
        );

        var slot = await Jobs.GetAsync(JobLookup.ById(slotId), ct);
        Assert.NotNull(slot);
        Assert.Equal(JobStatusCode.Ready, slot!.Status);
        Assert.Null(slot.LeasedByWorkerId);
    }
}
