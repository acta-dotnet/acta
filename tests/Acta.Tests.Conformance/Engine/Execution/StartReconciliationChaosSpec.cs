using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Execution;

/// <summary>
/// The window between a claim and its handler: the start write, and the reads the handler needs. The start matches
/// the claim on worker and execution number, so an operator verb that moves the row's version cannot refuse it, and a
/// start that committed and lost its answer is answered Started again when the write is repeated. A row recovery took
/// in this window is a clean skip, and the reads are repeated the way the writes are.
/// </summary>
[ConformanceSpec(
    "runtime.start-reconciliation",
    "A claim survives what happens between its claim and its handler",
    Area = "Runtime",
    Contract = "An operator verb does not refuse a start, a replayed start answers Started and runs once, a reclaimed row is a clean skip, and a failing setup read is repeated."
)]
public sealed class StartReconciliationChaosSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private StoreFaultPlan _faults = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(DisplayName = "A reprioritize between the claim and the start does not refuse the start")]
    public async Task Reprioritize_between_claim_and_start_is_absorbed()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The verb bumps runtimes.version on the Dispatched row without touching ownership or the execution.
        _faults.RunBeforeStartOnce(async () => await Jobs.ReprioritizeAsync(enqueued, JobPriorityCode.High, ct: ct));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
        Assert.Single(await GetEventsByJobId.Run(Services, enqueued.JobId, ct), e => e.EventCode == EventCode.JobExecutionStarted);
    }

    [Fact(DisplayName = "A start that fails before it commits, with a reprioritize in its retry window, lands on the retry")]
    public async Task Failed_start_then_reprioritize_then_retry_is_absorbed()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The first start never reaches the store and the version moves while the repeat waits.
        _faults.RunBeforeStartOnce(async () =>
        {
            await Jobs.ReprioritizeAsync(enqueued, JobPriorityCode.High, ct: ct);
            throw new InjectedProviderError("before StartExecution");
        });

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
        Assert.Single(await GetEventsByJobId.Run(Services, enqueued.JobId, ct), e => e.EventCode == EventCode.JobExecutionStarted);
    }

    [Fact(
        DisplayName = "A start that commits and loses its answer is answered Started again, runs the handler once, and writes one started event"
    )]
    public async Task Start_that_commits_and_loses_its_answer_is_not_made_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // The repeat finds the row already Executing under this worker at this execution.
        _faults.ThrowAfterStartOnce();

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
        Assert.Equal(1, ChaosProbes.CountingInvocations[enqueued.JobId]);
        Assert.Single(await GetEventsByJobId.Run(Services, enqueued.JobId, ct), e => e.EventCode == EventCode.JobExecutionStarted);
    }

    [Fact(DisplayName = "A row recovery reclaimed between the claim and the start is a clean skip")]
    public async Task Row_reclaimed_before_start_is_a_clean_skip()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);
        var namespaceId = await ChaosSpecHelpers.NamespaceIdAsync(Db, TestNamespace, ct);

        // Recovery takes the row before the start, so the start answers NotOwner on a row that is Ready and
        // unowned. The run-once helper then claims the row again within its budget; one handler run and one
        // started event say the refused start wrote nothing.
        var reclaimed = 0;
        _faults.RunBeforeStartOnce(async () =>
        {
            await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
            reclaimed = await ChaosSpecHelpers.ReclaimAsync(Services, namespaceId, ct);
        });

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(1, reclaimed);
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
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
