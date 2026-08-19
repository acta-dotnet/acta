using System.Collections.Concurrent;
using System.Diagnostics;
using Acta.Runtime.Modules.Execution.Workers;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class WorkerShutdownPhaseTests
{
    // The operations these tests hand the phase never complete on their own, so a phase that drops
    // its own timeout does not return late - it does not return at all. Waits that sit behind such an
    // operation are wrapped in this, purely so a hang fails with a readable message instead of
    // stalling the run until the host gives up. It measures nothing: it is sized to be unreachable by
    // scheduling noise on a loaded machine, and the fact about how promptly the phase gave up states
    // its own, far tighter bound.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    // The timeout the phase is configured with below, and the ceiling its return has to beat. Two
    // seconds is 40x the configured timeout - past any lateness a loaded runner can add to a single
    // 50ms timer - while sitting under the 5s timeout the other facts here use, so a phase that
    // silently falls back to some other bound fails this instead of sliding beneath it.
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan PhaseTimeoutCeiling = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Lifecycle_operations_start_concurrently()
    {
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        var run = WorkerShutdownPhase.RunAsync(
            [1, 2, 3],
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref started) == 3)
                {
                    allStarted.TrySetResult();
                }
                await release.Task.WaitAsync(ct);
            },
            TimeSpan.FromSeconds(5),
            static (_, _) => { },
            TestContext.Current.CancellationToken
        );

        // A hang guard, not a measurement: concurrency is the fact and the gate below reports it, so
        // this only has to be unreachable by scheduling noise on a loaded machine.
        await allStarted.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        release.TrySetResult();

        Assert.True(await run);
    }

    [Fact]
    public async Task Provider_ignoring_cancellation_cannot_hold_phase_past_timeout()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        // Two separate concerns: the WaitAsync makes a phase that never gives up fail readably rather
        // than hang, and the elapsed bound below is the actual fact - that it gave up on ITS timeout.
        var completed = await WorkerShutdownPhase
            .RunAsync([1, 2], (_, _) => never.Task, PhaseTimeout, static (_, _) => { }, TestContext.Current.CancellationToken)
            .WaitAsync(HangGuard, TestContext.Current.CancellationToken);

        Assert.False(completed);
        Assert.True(
            stopwatch.Elapsed < PhaseTimeoutCeiling,
            $"the phase took {stopwatch.Elapsed} against a {PhaseTimeout.TotalMilliseconds}ms timeout."
        );
    }

    [Fact]
    public async Task One_runtime_failure_is_observed_without_blocking_others()
    {
        var completedItems = new ConcurrentBag<int>();
        var failures = new ConcurrentBag<(int Item, Exception Error)>();

        var completed = await WorkerShutdownPhase.RunAsync(
            [1, 2, 3],
            (item, _) =>
            {
                if (item == 2)
                {
                    throw new InvalidOperationException("stamp failed");
                }
                completedItems.Add(item);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5),
            (item, error) => failures.Add((item, error)),
            TestContext.Current.CancellationToken
        );

        Assert.True(completed);
        Assert.Equal([1, 3], completedItems.Order());
        var (Item, Error) = Assert.Single(failures);
        Assert.Equal(2, Item);
        Assert.IsType<InvalidOperationException>(Error);
    }
}
