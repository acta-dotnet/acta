using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

[ConformanceSpec(
    "chaos.claim-control-races",
    "Claim and operator-control races have one legal final state",
    Area = "Chaos",
    Contract = "Concurrent claims and control verbs at dispatched/executing boundaries resolve to exactly one legal state with explicit events.",
    Arrange = "Probe jobs are enqueued in a namespace with two registered workers so claim and control verbs can collide at status boundaries.",
    Act = "Two workers race one claim, then pause is tried on a Dispatched job, restart and cancel on an Executing job, and pause then resume on a Ready job.",
    Assert = "Each race resolves to exactly one legal state: one claim wins, mid-flight pause and restart are rejected, and cancel and resume apply with explicit events."
)]
public abstract class ClaimAndControlRaceChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // A normal positive lease; these races resolve by claim ownership, not by lease expiry.
    private const int LeaseTtlSeconds = 60;

    [Fact(DisplayName = "Two claimers cannot both own one job")]
    public async Task Two_workers_race_to_claim_same_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker1 = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        _ = Services.GetRequiredService<ISqlDialect>();
        var (_, worker2) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            "race worker",
            "host-2",
            "test",
            "acta",
            "dotnet",
            1,
            1,
            ct
        );
        var enqueued = await ChaosSpecHelpers.EnqueueAddNumbersAsync(Services, Jobs, TestNamespace, ct);

        // --- 1. Two workers race to claim the same Ready row.
        var first = Services
            .GetRequiredService<IExecutionStore>()
            .ClaimOneAsync(new ClaimRequest(ns, worker1, MaxBatch: 1), LeaseTtlSeconds, enqueued.JobId, ct);
        var second = Services
            .GetRequiredService<IExecutionStore>()
            .ClaimOneAsync(new ClaimRequest(ns, worker2, MaxBatch: 1), LeaseTtlSeconds, enqueued.JobId, ct);
        await Task.WhenAll(first, second);

        // --- 2. Exactly one wins; the row is Dispatched to the winner with no events yet.
        var winners = first.Result.Jobs.Concat(second.Result.Jobs).ToList();
        var winner = Assert.Single(winners);
        var winningWorker = first.Result.Jobs.Count == 1 ? worker1 : worker2;

        // Read the runtime row directly to assert lease ownership and the execution number.
        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Dispatched, job!.Status);
        Assert.Equal(winningWorker, job.LeasedByWorkerId);
        Assert.Equal(winner.ExecutionNumber, job.ExecutionNumber);
        Assert.Empty(await GetEventsByJobId.Run(Services, enqueued.JobId, ct));
    }

    [Fact(DisplayName = "Pause while dispatched is rejected")]
    public async Task Pause_while_dispatched_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. Claim the row so it is Dispatched.
        Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker, LeaseTtlSeconds, enqueued, ct));

        // --- 2. Pause is rejected and leaves the row Dispatched with no JobPaused event.
        var pause = await Jobs.PauseAsync(enqueued, "race pause", ct: ct);
        Assert.Equal(JobControlAction.Rejected, pause.Action);
        Assert.Equal(JobStatusCode.Dispatched, pause.Status);
        Assert.Equal(JobStatusCode.Dispatched, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Empty((await GetEventsByJobId.Run(Services, enqueued.JobId, ct)).Where(e => e.JobEventCode == JobEventCode.JobPaused));
    }

    [Fact(DisplayName = "Restart while executing is rejected")]
    public async Task Restart_while_executing_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-blocking", ct);
        ChaosProbes.Reset(enqueued.JobId);

        // --- 1. Start the handler and let it block in Executing.
        var run = Runtime.RunOnceAsync(enqueued, ct);
        await ChaosProbes.WaitStartedAsync(enqueued.JobId, ct);

        // --- 2. Restart is rejected and leaves the row Executing with no JobRestarted event.
        var restart = await Jobs.RestartAsync(enqueued, "race restart", ct: ct);
        Assert.Equal(JobControlAction.Rejected, restart.Action);
        Assert.Equal(JobStatusCode.Executing, restart.Status);
        Assert.Equal(JobStatusCode.Executing, await Jobs.GetStatusAsync(enqueued, ct));
        Assert.Empty((await GetEventsByJobId.Run(Services, enqueued.JobId, ct)).Where(e => e.JobEventCode == JobEventCode.JobRestarted));

        // --- 3. Releasing the probe lets the original run finish Succeeded.
        ChaosProbes.Release(enqueued.JobId);
        Assert.Equal(RunOnceOutcome.Completed, await run);
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(enqueued, ct));
    }

    [Fact(DisplayName = "Cancel while executing records both execution-finished Cancelled and job-cancelled")]
    public async Task Cancel_while_executing_finishes_attempt_as_cancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-blocking", ct);
        ChaosProbes.Reset(enqueued.JobId);

        // --- 1. Start the handler and let it block in Executing.
        var run = Runtime.RunOnceAsync(enqueued, ct);
        await ChaosProbes.WaitStartedAsync(enqueued.JobId, ct);

        // --- 2. Cancel applies; the heartbeat then cancels the running handler.
        var cancel = await Jobs.CancelAsync(enqueued, "operator stop", ct: ct);
        Assert.Equal(JobControlAction.Applied, cancel.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancel.Status);
        await Runtime.RunHeartbeatOnceAsync(ct);
        await ChaosProbes.WaitCancelledAsync(enqueued.JobId, ct);

        Assert.Equal(RunOnceOutcome.NothingClaimed, await run);
        Assert.Equal(JobStatusCode.Cancelled, await Jobs.GetStatusAsync(enqueued, ct));

        // --- 3. Timeline: one start, the Cancelled execution-finished, and the job-cancelled event.
        // cancel_job emits the finished event (Executing->Cancelled) before JobCancelled when it
        // cancels an executing job, so both are deterministic here.
        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.Single(events.Where(e => e.JobEventCode == JobEventCode.JobExecutionStarted));
        var finished = Assert.Single(
            events.Where(e => e.JobEventCode == JobEventCode.JobExecutionFinished && e.ExecutionStatus == ExecutionStatusCode.Cancelled)
        );
        Assert.Equal(JobStatusCode.Executing, finished.FromStatus);
        Assert.Equal(JobStatusCode.Cancelled, finished.ToStatus);
        Assert.Single(events.Where(e => e.JobEventCode == JobEventCode.JobCancelled));
    }

    [Fact(DisplayName = "Resume after pause returns to Ready")]
    public async Task Resume_after_pause_returns_to_ready_and_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. Pause a Ready job, then resume it back to Ready.
        var pause = await Jobs.PauseAsync(enqueued, "pause race", ct: ct);
        Assert.Equal(JobControlAction.Applied, pause.Action);
        Assert.Equal(JobStatusCode.Paused, pause.Status);

        var resume = await Jobs.ResumeAsync(enqueued, "resume race", ct: ct);
        Assert.Equal(JobControlAction.Applied, resume.Action);
        Assert.Equal(JobStatusCode.Ready, resume.Status);

        // --- 2. The resumed job runs to completion, with paired pause/resume events.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        Assert.Single(
            events.Where(e =>
                e.JobEventCode == JobEventCode.JobPaused && e.FromStatus == JobStatusCode.Ready && e.ToStatus == JobStatusCode.Paused
            )
        );
        Assert.Single(
            events.Where(e =>
                e.JobEventCode == JobEventCode.JobResumed && e.FromStatus == JobStatusCode.Paused && e.ToStatus == JobStatusCode.Ready
            )
        );
    }
}
