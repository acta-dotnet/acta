using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

[ConformanceSpec(
    "chaos.claim-control-races",
    "Claim and operator-control races have one legal final state",
    Area = "Chaos",
    Contract = "Concurrent claims and control verbs at the executing boundary resolve to exactly one legal state with explicit events."
)]
public sealed class ClaimAndControlRaceChaosSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
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
            .GetRequiredService<ExecutionOperations>()
            .ClaimJobAsync(new ClaimOptions(ns, worker1, MaxBatch: 1), LeaseTtlSeconds, enqueued.JobId, ct);
        var second = Services
            .GetRequiredService<ExecutionOperations>()
            .ClaimJobAsync(new ClaimOptions(ns, worker2, MaxBatch: 1), LeaseTtlSeconds, enqueued.JobId, ct);
        await Task.WhenAll(first, second);

        // --- 2. Exactly one wins; the row is Executing under the winner, on its first attempt.
        var winners = (await first).Jobs.Concat((await second).Jobs).ToList();
        var winner = Assert.Single(winners);
        var winningWorker = (await first).Jobs.Count == 1 ? worker1 : worker2;

        // Read the jobs_state row directly to assert lease ownership and the execution number.
        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, job!.Status);
        Assert.Equal(winningWorker, job.LeasedByWorkerId);
        Assert.Equal(1, job.ExecutionNumber);
        Assert.Equal(winner.ExecutionNumber, job.ExecutionNumber);
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
        Assert.Equal(ControlAction.Rejected, restart.Action);
        Assert.Equal(JobStatusCode.Executing, restart.Status);
        Assert.Equal(JobStatusCode.Executing, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
        Assert.DoesNotContain(await GetEventsByJobId.Run(Services, enqueued.JobId, ct), e => e.EventCode == EventCode.JobRestarted);

        // --- 3. Releasing the probe lets the original run finish Succeeded.
        ChaosProbes.Release(enqueued.JobId);
        Assert.Equal(RunOnceStatus.Completed, await run);
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);
    }

    [Fact(DisplayName = "Cancel while executing records both the attempt yielded as cancelled and job-cancelled")]
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
        Assert.Equal(ControlAction.Applied, cancel.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancel.Status);
        await Runtime.RunHeartbeatOnceAsync(ct);
        await ChaosProbes.WaitCancelledAsync(enqueued.JobId, ct);

        Assert.Equal(RunOnceStatus.NothingClaimed, await run);
        Assert.Equal(JobStatusCode.Cancelled, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);

        // --- 3. Timeline: the attempt yielded as cancelled, then the job-cancelled event.
        // cancel_job ends the executing attempt before it writes JobCancelled, so both are deterministic here.
        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var yielded = Assert.Single(events, e => e.EventCode == EventCode.JobExecutionYielded);
        Assert.Equal("cancelled", yielded.Outcome);
        var cancelled = Assert.Single(events, e => e.EventCode == EventCode.JobCancelled);
        Assert.True(yielded.Id < cancelled.Id);
    }

    [Fact(DisplayName = "Resume after pause returns to Ready")]
    public async Task Resume_after_pause_returns_to_ready_and_runs()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct);

        // --- 1. Pause a Ready job, then resume it back to Ready.
        var pause = await Jobs.PauseAsync(enqueued, "pause race", ct: ct);
        Assert.Equal(ControlAction.Applied, pause.Action);
        Assert.Equal(JobStatusCode.Paused, pause.Status);

        var resume = await Jobs.ResumeAsync(enqueued, "resume race", ct: ct);
        Assert.Equal(ControlAction.Applied, resume.Action);
        Assert.Equal(JobStatusCode.Ready, resume.Status);

        // --- 2. The resumed job runs to completion, with paired pause/resume events.
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var paused = Assert.Single(events, e => e.EventCode == EventCode.JobPaused);
        var resumed = Assert.Single(events, e => e.EventCode == EventCode.JobResumed);
        Assert.True(paused.Id < resumed.Id);
    }
}
