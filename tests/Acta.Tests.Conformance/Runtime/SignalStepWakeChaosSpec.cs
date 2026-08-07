using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

[ConformanceSpec(
    "chaos.signal-step-wake",
    "Signals, step exhaustion, and lost wakes converge without timing assumptions",
    Area = "Chaos",
    Contract = "Signals raised before or after waiter creation, step retry exhaustion, and lost wake notifications each produce one legal final state.",
    Arrange = "Signal and step probes run with system jobs disabled and a 5-minute safety poll under a controlled wakeup.",
    Act = "Signals are raised before and after waiter creation, a step exhausts its retries, and a wake notification is dropped.",
    Assert = "Each path converges to one legal final state, with the pre-set signal consumed without suspending and the lost wake recovered by polling."
)]
public abstract class SignalStepWakeChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private ControlledWakeup _wakeup = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _wakeup = services.AddControlledWakeup();
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterFrameworkJobs = false;
            o.SafetyPollInterval = TimeSpan.FromMinutes(5);
        });
    }

    [Fact(DisplayName = "Signal raised before a waiter exists is consumed without suspending")]
    public async Task Signal_before_waiter_exists_is_consumed_without_suspend()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-wait-signal", ct);

        // --- 1. Raise the signal before any waiter exists; the job stays Ready.
        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Ready, raise.Status);

        // --- 2. One tick consumes the pre-set signal and completes without suspending.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.Single(
            events.Where(e =>
                e.JobEventCode == JobEventCode.JobSignalRaised && e.FromStatus == JobStatusCode.Ready && e.ToStatus == JobStatusCode.Ready
            )
        );
        Assert.DoesNotContain(events, e => e.JobEventCode == JobEventCode.JobSuspended);
        Assert.DoesNotContain(events, e => e.JobEventCode == JobEventCode.JobResumed);
    }

    [Fact(DisplayName = "Signal raised after a waiter exists resumes the suspended job")]
    public async Task Signal_after_waiter_exists_resumes_suspended_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-wait-signal", ct);

        // --- 1. First tick creates the waiter and suspends the job.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Suspended, await Jobs.GetStatusAsync(enqueued, ct));

        // --- 2. Raising the signal resumes the job; the next tick completes it.
        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Ready, raise.Status);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.Single(
            events.Where(e =>
                e.JobEventCode == JobEventCode.JobSuspended
                && e.FromStatus == JobStatusCode.Executing
                && e.ToStatus == JobStatusCode.Suspended
            )
        );
        // RaiseSignal records the raise without moving status (from == to); the Suspended->Ready
        // transition is the separate JobResumed event below.
        Assert.Single(
            events.Where(e =>
                e.JobEventCode == JobEventCode.JobSignalRaised
                && e.FromStatus == JobStatusCode.Suspended
                && e.ToStatus == JobStatusCode.Suspended
            )
        );
        Assert.Single(
            events.Where(e =>
                e.JobEventCode == JobEventCode.JobResumed && e.FromStatus == JobStatusCode.Suspended && e.ToStatus == JobStatusCode.Ready
            )
        );
    }

    [Fact(DisplayName = "Step retry exhaustion fails the parent exactly once")]
    public async Task Step_retry_exhaustion_fails_parent_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "job-step-exhaust", ct);

        // --- 1. The step retries once, then exhausts its budget and fails the parent.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        // --- 2. The step slot is Exhausted after two attempts (no read operation, read the row).
        var step = Assert.Single(await Db.From<JobStep>().Where(a => a.JobId == enqueued.JobId).ToListAsync(ct));
        Assert.Equal(JobStepStatusCode.Exhausted, step.Status);
        Assert.Equal((short)2, step.AttemptNumber);

        // --- 3. The parent is Failed with the step's failure reason.
        var job = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(job);
        Assert.Equal(JobStatusCode.Failed, job!.Status);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var finished = events.Where(e => e.JobEventCode == JobEventCode.JobExecutionFinished).OrderByDescending(e => e.Id).First();
        Assert.Equal(JobEventReasonCode.JobUnhandledException, finished.JobEventReasonCode);
        Assert.Single(events.Where(e => e.JobEventCode == JobEventCode.JobRescheduled));
        Assert.Equal(2, JobStepProbes.BodyInvocations[enqueued.JobId]);
    }

    [Fact(DisplayName = "Lost wake notification is recovered by the safety poll path")]
    public async Task Wake_notification_lost_is_recovered_by_poll_path()
    {
        var ct = TestContext.Current.CancellationToken;
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        await _wakeup.WaiterReady.WaitAsync(ct);

        // --- 1. Enqueue wakes the loop, then we drop the wake to force the poll path.
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);
        Assert.True(_wakeup.WakeCount > 0);
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(enqueued, ct));
        _wakeup.ReleaseWait(WorkerWakeupWaitResult.TimedOut);

        // --- 2. The safety poll claims and completes the job without a delivered wake. The
        // discriminator is the JobCompletion wake, published only after the terminal completion
        // write returns: awaiting it proves the poll path ran the job to completion, however slowly
        // a loaded runner gets there (the old 5s wall-clock budget timed the whole
        // claim->execute->complete round trip and flaked under full-suite SQLite load).
        await _wakeup.WaitForWakeAsync(WorkerWakeupChannelKind.JobCompletion, threshold: 1, ct);

        await loopCts.CancelAsync();
        await loop;
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
    }
}
