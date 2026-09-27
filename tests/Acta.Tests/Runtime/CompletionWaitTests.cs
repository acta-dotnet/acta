using System.Diagnostics;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Workers;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The enqueue-and-wait loop against the in-process wakeup, no DB: a completion that commits while the
/// status read is in flight still wakes the waiter, and a read that finds the job terminal leaves no
/// sleep behind.
/// </summary>
public sealed class CompletionWaitTests
{
    private const long JobId = 42;

    // Far beyond the budget, so no poll can rescue a missed wake.
    private static readonly TimeSpan NoPoll = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private sealed record Snapshot(bool Terminal);

    [Fact]
    public async Task A_completion_that_lands_during_the_read_wakes_the_waiter()
    {
        var wakeup = new InProcessWakeup();
        var reads = 0;

        // The first read sees the job running, and its completion commits and publishes before the read
        // returns; the second read sees it finished.
        async ValueTask<Snapshot?> Read(CancellationToken ct)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref reads) == 1)
            {
                await wakeup.WakeAsync(WorkerWakeupChannel.JobCompletion(JobId), WorkerWakeupReason.JobFinished, ct);
                return new Snapshot(Terminal: false);
            }

            return new Snapshot(Terminal: true);
        }

        var started = Stopwatch.GetTimestamp();
        var (snapshot, timedOut) = await CompletionWait.AwaitAsync(
            wakeup,
            JobId,
            Read,
            static s => s.Terminal,
            Budget,
            NoPoll,
            TestContext.Current.CancellationToken
        );

        Assert.False(timedOut);
        Assert.True(snapshot!.Terminal);
        Assert.Equal(2, reads);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5), "the wait fell through to its budget");
    }

    [Fact]
    public async Task A_terminal_first_read_returns_and_leaves_no_sleep_behind()
    {
        var wakeup = new InProcessWakeup();

        var (snapshot, timedOut) = await CompletionWait.AwaitAsync(
            wakeup,
            JobId,
            static _ => ValueTask.FromResult<Snapshot?>(new Snapshot(Terminal: true)),
            static s => s.Terminal,
            Budget,
            NoPoll,
            TestContext.Current.CancellationToken
        );

        Assert.False(timedOut);
        Assert.True(snapshot!.Terminal);

        // The abandoned sleep retired its waiter, so a later wait on the channel starts clean.
        Assert.Equal(
            WorkerWakeupWaitStatus.TimedOut,
            await wakeup.WaitAsync(WorkerWakeupChannel.JobCompletion(JobId), TimeSpan.FromMilliseconds(50), CancellationToken.None)
        );
    }

    [Fact]
    public async Task A_job_that_never_finishes_times_out_on_the_budget()
    {
        var (snapshot, timedOut) = await CompletionWait.AwaitAsync(
            new InProcessWakeup(),
            JobId,
            static _ => ValueTask.FromResult<Snapshot?>(new Snapshot(Terminal: false)),
            static s => s.Terminal,
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(50),
            TestContext.Current.CancellationToken
        );

        Assert.True(timedOut);
        Assert.False(snapshot!.Terminal);
    }
}
