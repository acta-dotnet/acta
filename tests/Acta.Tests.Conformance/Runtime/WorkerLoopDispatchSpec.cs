using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Exercises the production claim/dispatch loop (<see cref="WorkerRuntime.RunLoopAsync"/>): a single
/// claim producer feeds a bounded channel that N executor loops drain concurrently. Enqueues a
/// backlog, runs the loop in the background until every row reaches Succeeded, then cancels and asserts a
/// clean shutdown (the loop completes the channel and awaits its executors - no fault, no hang).
/// The RunOnceAsync-driven specs cover the per-job execute body; this covers the loop wiring,
/// including the idle-sleep contract: an in-process wakeup interrupts the sleep on enqueue, a
/// delayed enqueue refreshes a sleeping loop's horizon, a completion that re-arms publishes its own
/// wake, and a row made Ready with no publish at all is still discovered by the safety poll.
/// Identical assertions run against SqlServer and Postgres via the provider one-liners.
/// </summary>
[ConformanceSpec(
    "worker-loop.dispatch",
    "The run loop drains a backlog, wakes on publishes, and shuts down cleanly",
    Area = "Execution",
    Contract = "RunLoopAsync drains a backlog, sleeps idle until the claim horizon capped by SafetyPollInterval, wakes early on wakeup publishes, and cancels cleanly.",
    Arrange = "A backlog is enqueued with an 8s SafetyPollInterval so wakeup-driven pickups are distinguishable from safety polls.",
    Act = "RunLoopAsync runs in the background across enqueues, delayed rows, colocated completions, retries, and an unpublished Ready row.",
    Assert = "The loop drains the backlog to Succeeded, wakes early on wakeup publishes, discovers the unpublished row via the safety poll, and cancels cleanly."
)]
public abstract class WorkerLoopDispatchSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Sized for the fallback fact, the only one that waits a safety poll out. The wake facts read how
    // the idle sleep ENDED rather than racing its length, so its exact value cannot decide them.
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(8);

    private WakeupParkProbe _wakeup = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        _wakeup = services.AddWakeupParkProbe();
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterSystemJobs = false;
            o.SafetyPollInterval = SafetyPoll;
        });
    }

    [Fact(DisplayName = "Backlog drains to Succeeded and cancellation completes the channel and awaits executors cleanly")]
    public async Task Run_loop_drains_a_backlog_and_shuts_down_cleanly()
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = JobPayload.Json(new AddNumbers(2, 3));

        const int backlog = 12;
        var ids = new long[backlog];
        for (var i = 0; i < backlog; i++)
        {
            var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "add-numbers", payload), ct);
            ids[i] = enqueued.JobId;
        }

        // Drive the real loop in the background under a loop-scoped token, so cancelling it doesn't
        // cancel the test's own ct.
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), ids, ct);

        await loopCts.CancelAsync();
        await loop; // Clean shutdown: producer breaks, writer completes, executors drain and return.

        foreach (var id in ids)
        {
            var status = await Services.GetRequiredService<IJobStore>().GetJobStatusAsync(id, ct);
            Assert.Equal(JobStatusCode.Succeeded, status);
        }
    }

    [Fact(DisplayName = "A due-now enqueue wake interrupts the idle sleep")]
    public async Task A_due_now_enqueue_interrupts_the_idle_sleep()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = await StartParkedLoopAsync(loopCts.Token, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), [enqueued.JobId], ct);

        await loopCts.CancelAsync();
        await loop;

        // The sleep the loop was parked in ended in a wake, so the enqueue publish interrupted it;
        // the safety poll is the only other way out and it reports itself as a timeout.
        Assert.Equal(WorkerWakeupWaitResult.Signaled, ParkedSleepOutcome());
    }

    [Fact(DisplayName = "A delayed enqueue refreshes the sleeping loop's horizon")]
    public async Task A_delayed_enqueue_refreshes_the_sleeping_loops_horizon()
    {
        var ct = TestContext.Current.CancellationToken;

        // The loop's first empty claim sees NO Ready rows at all (null horizon) and sleeps the full
        // safety interval - the one state a sentinel alone cannot get it out of early.
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = await StartParkedLoopAsync(loopCts.Token, ct);

        var dbNow = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(2, 3)),
                NextRunAtUtc: dbNow.AddMilliseconds(1500)
            ),
            ct
        );

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), [enqueued.JobId], ct);

        await loopCts.CancelAsync();
        await loop;

        // The HorizonChanged publish ended the full safety sleep in a wake; the loop's next empty
        // claim then read the ~1.5s horizon and slept to the due instant. That second sleep is meant
        // to time out, which is why only the parked sleep's outcome is the fact here.
        Assert.Equal(WorkerWakeupWaitResult.Signaled, ParkedSleepOutcome());
    }

    [Fact(DisplayName = "An unpublished Ready row is discovered by the safety poll")]
    public async Task An_unpublished_ready_row_is_discovered_by_the_safety_poll()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = await StartParkedLoopAsync(loopCts.Token, ct);

        // Insert straight through the enqueue operation, bypassing JobsApi - the wakeup publish
        // never reaches this process's transport, simulating a writer in another process with no
        // shared transport. The safety poll is the only discovery path.
        var rows = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3)))],
            ct
        );
        var jobId = rows[0].JobId;

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), [jobId], ct);

        await loopCts.CancelAsync();
        await loop;

        // No publish reached the loop, so the sleep it was parked in ran out instead of being woken:
        // the timeout IS safety-poll discovery, where an elapsed-time lower bound could only infer it.
        Assert.Equal(WorkerWakeupWaitResult.TimedOut, ParkedSleepOutcome());
    }

    [Fact(DisplayName = "ExecuteAndWaitAsync observes a colocated completion at wake speed")]
    public async Task Execute_observes_a_colocated_completion_at_wake_speed()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = await StartParkedLoopAsync(loopCts.Token, ct);

        // PollInterval is deliberately huge so a poll-driven return is impossible inside the wait
        // timeout: the waiter either returns on its JobCompletion wake or times out.
        var outcome = await Jobs.ExecuteAndWaitAsync(
            new AddNumbers(2, 3),
            new JobExecutionOptions { PollInterval = TimeSpan.FromSeconds(15), WaitTimeout = TimeSpan.FromSeconds(20) },
            ct
        );

        await loopCts.CancelAsync();
        await loop;

        Assert.True(outcome.IsSuccess, $"Execute outcome: timedOut={outcome.IsTimedOut}, status={outcome.TerminalStatus}.");
        // Not one completion wait fell through to its poll interval, so the colocated completion's
        // wake is what returned this call.
        Assert.DoesNotContain(WorkerWakeupWaitResult.TimedOut, _wakeup.WaitsOn(WorkerWakeupChannelKind.JobCompletion));
    }

    [Fact(DisplayName = "A re-arming completion wakes the loop for its own retry")]
    public async Task A_re_arming_completion_wakes_the_loop_for_its_own_retry()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = await StartParkedLoopAsync(loopCts.Token, ct);

        // flaky-recover fails its first attempts with a zero backoff: each failing completion lands
        // the row Ready due-now, and complete_execution's final-Ready report is the ONLY publish
        // site that can see that transition. Without it every retry would wait out the safety poll.
        FlakyRecoverProbe.Reset(TestNamespace);
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "flaky-recover", JobPayload.None), ct);

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), [enqueued.JobId], ct);

        await loopCts.CancelAsync();
        await loop;

        // Every idle sleep across the whole retry chain ended in a wake, so no attempt was found by
        // the safety poll. One elapsed-time budget for the chain could not say which link was slow.
        Assert.DoesNotContain(WorkerWakeupWaitResult.TimedOut, _wakeup.WaitsOn(WorkerWakeupChannelKind.WorkerNamespace));
    }

    // Starts the real loop and returns once its first empty claim has parked in the idle sleep. Every
    // fact below acts on a loop that is IN that sleep, and a fixed warm-up cannot promise it: a first
    // claim still in flight would claim the new row itself, answering the question with a plain claim.
    private async Task<Task> StartParkedLoopAsync(CancellationToken loopCt, CancellationToken ct)
    {
        var loop = Runtime.RunLoopAsync(loopCt);
        await _wakeup.Parked.WaitAsync(TimeSpan.FromSeconds(30), ct);
        return loop;
    }

    // How the idle sleep the loop was parked in when the fact acted ended: a wake or the safety poll.
    private WorkerWakeupWaitResult ParkedSleepOutcome()
    {
        var sleeps = _wakeup.WaitsOn(WorkerWakeupChannelKind.WorkerNamespace);
        Assert.NotEmpty(sleeps);
        return sleeps[0];
    }

    private static async Task WaitUntilAllDoneAsync(IJobStore store, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var done = 0;
            foreach (var id in ids)
            {
                var status = await store.GetJobStatusAsync(id, ct);
                if (status == JobStatusCode.Succeeded)
                {
                    done++;
                }
            }
            if (done == ids.Count)
            {
                return;
            }
            await Task.Delay(50, ct);
        }
        Assert.Fail("Backlog did not drain to Succeeded within the timeout: the dispatch loop stalled.");
    }
}
