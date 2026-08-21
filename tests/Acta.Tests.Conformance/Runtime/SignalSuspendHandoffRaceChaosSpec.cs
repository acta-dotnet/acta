using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The suspend handoff: the window between <c>wait_signal</c> answering SuspendPending and
/// <c>complete_execution</c> writing that suspend. A raise landing inside it answers a wait the ledger
/// has no record of, so the completion re-reads the awaited slot under its own lock and lands the job
/// Ready rather than parking it on a signal that already arrived.
/// </summary>
/// <remarks>
/// Deterministic by construction. The raise runs inside the completion hook, so the interleaving is
/// this spec's choice rather than whichever connection happened to win a window microseconds wide.
/// </remarks>
[ConformanceSpec(
    "signals.suspend-handoff-race",
    "A raise inside the suspend handoff lands the job Ready, not Suspended",
    Area = "Signals",
    Contract = "complete_execution re-reads the awaited slot under lock, so a raise that beat the suspend write lands the job Ready and claimable, payload intact.",
    Arrange = "A job waiting on a typed signal runs with the raise issued from inside the completion window, before the suspend reaches the ledger.",
    Act = "The handler suspends on its wait, the signal is raised while the completion is held, and the completion is then allowed to run.",
    Assert = "The job lands Ready and claimable with no suspend on its timeline, a wake is published, and the next tick consumes the Set slot with its payload."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.RaiseSignalAsync))]
public abstract class SignalSuspendHandoffRaceChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string JobName = "job-wait-signal-typed";
    private const string SignalName = "review";

    private StoreFaultPlan _faults = null!;
    private ControlledWakeup _wakeup = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();

        // Every tick here is a RunOnceAsync, so the double is never waited on; it is here only to count
        // the publishes, which is the difference between a Ready row a worker learns about and one that
        // sits until the safety poll.
        _wakeup = services.AddControlledWakeup();
    }

    [Fact(DisplayName = "A raise landing between the wait and its completion lands the job Ready with the payload intact")]
    public async Task Raise_inside_the_suspend_handoff_lands_the_job_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, JobName, JobPayload.None), ct);
        var decision = new ReviewDecision(true, "beat the handoff");
        // Baselined after the enqueue's own wake, which lands on this same channel. Inside the window
        // the raise publishes nothing (the job is still Executing), so complete_execution's Ready
        // transition is the only publisher left and any increase here is that transition.
        var namespaceWakesBefore = _wakeup.WakeCountFor(WorkerWakeupChannelKind.WorkerNamespace);

        // By the time this runs the handler has thrown its suspend and wait_signal has committed the
        // Pending slot, but the completion carrying that suspend is built and unsent. That is the
        // window, and it is the only place a raise can be neither too early nor too late.
        _faults.RunBeforeCompleteOnce(async () =>
        {
            var raise = await Jobs.RaiseSignalAsync(enqueued, SignalName, decision, ct: ct);
            Assert.Equal(ControlAction.Applied, raise.Action);

            // Still Executing, so the raise has no Suspended row to release: it can set the slot and
            // nothing more. Whether the job runs again is the completion's re-read to decide.
            Assert.Equal(JobStatusCode.Executing, raise.Status);
        });

        // The attempt asked to suspend - Rearmed is what a suspend reports - and the routine overruled
        // it off the slot.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.NotNull(job.NextRunAtUtc);
        Assert.Null(job.LeasedByWorkerId);

        // A suspend that was overruled must not be on the timeline either, or an operator reads a job
        // parked on a signal it was already holding.
        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.DoesNotContain(events, e => e.EventCode == EventCode.JobSuspended);
        Assert.DoesNotContain(events, e => e.EventCode == EventCode.JobResumed);
        Assert.Single(events.Where(e => e.EventCode == EventCode.JobSignalRaised));

        // The other half of not losing the wake: complete_execution is the only site that can see this
        // transition, so a Ready row it reports without a publish is a job nobody is told about.
        Assert.True(_wakeup.WakeCountFor(WorkerWakeupChannelKind.WorkerNamespace) > namespaceWakesBefore);

        // And the wake is not the claim: the next tick replays the handler over the Set slot and
        // finishes with the payload the raise carried, so nothing was consumed by the race.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(decision, await Jobs.GetResultAsync<ReviewDecision>(enqueued, ct));
        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
    }

    [Fact(DisplayName = "The same completion with nothing raised in the window still parks the job Suspended")]
    public async Task Handoff_with_no_raise_still_suspends_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, JobName, JobPayload.None), ct);

        // The slot re-read is a decision, not a path that always lands Ready. Without it asserted here,
        // the fact above would pass just as well against a completion that ignored the suspend.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, job.Status);
        Assert.Null(job.NextRunAtUtc);
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.Single(events.Where(e => e.EventCode == EventCode.JobSuspended));
    }
}
