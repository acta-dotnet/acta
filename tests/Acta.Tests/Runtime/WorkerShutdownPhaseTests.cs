using System.Collections.Concurrent;
using System.Diagnostics;
using Acta.Runtime.Modules.Execution.Workers;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class WorkerShutdownPhaseTests
{
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
            TestContext.Current.CancellationToken,
            static (_, _) => { }
        );

        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        release.TrySetResult();

        Assert.True(await run);
    }

    [Fact]
    public async Task Provider_ignoring_cancellation_cannot_hold_phase_past_timeout()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();

        var completed = await WorkerShutdownPhase.RunAsync(
            [1, 2],
            (_, _) => never.Task,
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken,
            static (_, _) => { }
        );

        Assert.False(completed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
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
            TestContext.Current.CancellationToken,
            (item, error) => failures.Add((item, error))
        );

        Assert.True(completed);
        Assert.Equal([1, 3], completedItems.Order());
        var (Item, Error) = Assert.Single(failures);
        Assert.Equal(2, Item);
        Assert.IsType<InvalidOperationException>(Error);
    }
}
