using System.Diagnostics;
using Acta.Configuration;
using Acta.Features.Jobs;
using Acta.Features.Workers;
using Acta.Payloads;
using Acta.Services.Locks;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Exercises the production claim/dispatch loop (<see cref="WorkerRuntime.RunLoopAsync"/>): a single
/// claim producer feeds a bounded channel that N executor loops drain concurrently. Enqueues a
/// backlog, runs the loop in the background until every row reaches Done, then cancels and asserts a
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
    Contract = "RunLoopAsync drains a backlog to Done, sleeps idle until the claim horizon capped by SafetyPollInterval, wakes early on wakeup publishes, and cancels cleanly.",
    Arrange = "A backlog is enqueued with an 8s SafetyPollInterval so wakeup-driven pickups are distinguishable from safety polls.",
    Act = "RunLoopAsync runs in the background across enqueues, delayed rows, colocated completions, retries, and an unpublished Ready row.",
    Assert = "The loop drains the backlog to Done, wakes early on wakeup publishes, discovers the unpublished row via the safety poll, and cancels cleanly."
)]
public abstract class WorkerLoopDispatchSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Long enough that a wakeup-driven pickup is unmistakably not a safety-poll pickup, short enough
    // that the fallback fact (which must wait it out) stays test-sized.
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(8);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterFrameworkJobs = false;
            o.SafetyPollInterval = SafetyPoll;
        });
    }

    [Fact(DisplayName = "Backlog drains to Done and cancellation completes the channel and awaits executors cleanly")]
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
            Assert.Equal(JobStatusCode.Done, status);
        }
    }

    [Fact(DisplayName = "A due-now enqueue wake interrupts the idle sleep")]
    public async Task A_due_now_enqueue_interrupts_the_idle_sleep()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        // Let the loop take its first empty claim and enter the long idle sleep (no Ready rows, so
        // it would otherwise wake only at the safety poll).
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var enqueuedAt = Stopwatch.GetTimestamp();
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), [enqueued.JobId], ct);
        var elapsed = Stopwatch.GetElapsedTime(enqueuedAt);

        await loopCts.CancelAsync();
        await loop;

        // Pickup must be wakeup-driven: far inside the 8s safety interval the sleeping loop was
        // otherwise committed to.
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"Pickup took {elapsed} — the enqueue publish did not interrupt the idle sleep.");
    }

    [Fact(DisplayName = "A delayed enqueue refreshes the sleeping loop's horizon")]
    public async Task A_delayed_enqueue_refreshes_the_sleeping_loops_horizon()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        // The loop's first empty claim sees NO Ready rows at all (null horizon) and sleeps the full
        // safety interval - the one state a sentinel alone cannot get it out of early.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var dbNow = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        var enqueuedAt = Stopwatch.GetTimestamp();
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
        var elapsed = Stopwatch.GetElapsedTime(enqueuedAt);

        await loopCts.CancelAsync();
        await loop;

        // The HorizonChanged publish wakes the loop; its next empty claim reads the ~1.5s horizon
        // and sleeps to the due instant instead of finishing the original 8s safety sleep.
        Assert.True(
            elapsed < TimeSpan.FromSeconds(6),
            $"Pickup took {elapsed} — the delayed enqueue did not refresh the sleeping loop's horizon."
        );
    }

    [Fact(DisplayName = "An unpublished Ready row is discovered by the safety poll")]
    public async Task An_unpublished_ready_row_is_discovered_by_the_safety_poll()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        // Insert straight through the enqueue operation, bypassing JobsApi - the wakeup publish
        // never reaches this process's transport, simulating a writer in another process with no
        // shared transport. The safety poll is the only discovery path.
        var enqueuedAt = Stopwatch.GetTimestamp();
        var rows = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3)))],
            ct
        );
        var jobId = rows[0].JobId;

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), [jobId], ct);
        var elapsed = Stopwatch.GetElapsedTime(enqueuedAt);

        await loopCts.CancelAsync();
        await loop;

        // No publish reached the loop, so pickup waited out (most of) the in-flight safety sleep -
        // the lower bound proves the poll, not a signal, found the row.
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(500),
            $"Pickup took only {elapsed} — expected safety-poll discovery, not a wakeup."
        );
    }

    [Fact(DisplayName = "ExecuteAndWaitAsync observes a colocated completion at wake speed")]
    public async Task Execute_observes_a_colocated_completion_at_wake_speed()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        // PollInterval is deliberately huge: without the JobCompletion wake the waiter's next
        // status read cannot land before ~15s, so any return under the 10s ceiling proves the wake,
        // not polling. The 5s dead zone between them absorbs slow CI runners (a loaded runner has
        // been observed taking 4.2s on the wake path); the wake path never waits on PollInterval,
        // so the passing case stays wake-fast regardless of the interval.
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = await Jobs.ExecuteAndWaitAsync(
            new AddNumbers(2, 3),
            new JobExecutionOptions { PollInterval = TimeSpan.FromSeconds(15), WaitTimeout = TimeSpan.FromSeconds(20) },
            ct
        );
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        await loopCts.CancelAsync();
        await loop;

        Assert.True(outcome.IsSuccess, $"Execute outcome: timedOut={outcome.IsTimedOut}, status={outcome.TerminalStatus}.");
        Assert.True(
            elapsed < TimeSpan.FromSeconds(10),
            $"Execute returned after {elapsed} — the completion wake did not interrupt the poll wait."
        );
    }

    [Fact(DisplayName = "A re-arming completion wakes the loop for its own retry")]
    public async Task A_re_arming_completion_wakes_the_loop_for_its_own_retry()
    {
        var ct = TestContext.Current.CancellationToken;

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        // flaky-recover fails its first attempts with a zero backoff: each failing completion lands
        // the row Ready due-now, and complete_execution's final-Ready report is the ONLY publish
        // site that can see that transition. Without it every retry would wait out the safety poll.
        FlakyRecoverProbe.Reset(TestNamespace);
        var enqueuedAt = Stopwatch.GetTimestamp();
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "flaky-recover", JobPayload.None), ct);

        await WaitUntilAllDoneAsync(Services.GetRequiredService<IJobStore>(), [enqueued.JobId], ct);
        var elapsed = Stopwatch.GetElapsedTime(enqueuedAt);

        await loopCts.CancelAsync();
        await loop;

        Assert.True(elapsed < TimeSpan.FromSeconds(6), $"Recovery took {elapsed} — the re-arming completion did not wake the loop.");
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
                if (status == JobStatusCode.Done)
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
        Assert.Fail("Backlog did not drain to Done within the timeout — the dispatch loop stalled.");
    }
}
