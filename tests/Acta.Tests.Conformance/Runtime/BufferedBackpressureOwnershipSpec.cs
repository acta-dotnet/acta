using Acta.Relational.Entities;
using Acta.Runtime.Hosting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The heartbeat's orphan release against a Buffered claim loop that is blocked on a full channel.
/// A claimed batch is this worker's the moment the claim commits, but the loop hands the rows to the
/// channel one at a time and a write blocks while every executor is held. The rows behind the blocked
/// write must already count as buffered, or two heartbeats would take them for a lost claim answer
/// and return them to Ready while they waited for their turn. A drain that lands while the write is
/// blocked hands those same rows back, since no executor will ever take them.
/// </summary>
[ConformanceSpec(
    "runtime.buffered-backpressure-ownership",
    "Rows waiting behind a full Buffered channel are not released as orphans",
    Area = "Runtime",
    Contract = "A row claimed but not written to a full Buffered channel is left alone by the heartbeat and handed back to Ready by a drain.",
    Arrange = "A Buffered worker with one executor held by a blocking job, a claim batch larger than the channel's free space, and several claimable jobs.",
    Act = "The heartbeat ticks twice while the claim loop is blocked on the full channel, or a drain begins there.",
    Assert = "No waiting row is rescheduled by the heartbeats, and the drain reschedules the rows it never wrote."
)]
[CoversStoreMethod(
    typeof(Acta.Runtime.Modules.Execution.IExecutionStore),
    nameof(Acta.Runtime.Modules.Execution.IExecutionStore.ClaimBatchAsync)
)]
public abstract class BufferedBackpressureOwnershipSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Six claimable rows behind the holding job: the channel holds three, so the second batch's write
    // blocks with rows behind it, and the sixth row stays Ready because the loop never gets to it.
    private const int WaitingJobs = 6;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = ExecutionProfile.Buffered;
            o.MaxConcurrentExecutors = 1;
            o.ClaimBatchSize = 3;
            o.SafetyPollInterval = TimeSpan.FromSeconds(1);
            // A long beat keeps the runtime's own heartbeat out of the window; the fact ticks it by hand.
            o.HeartbeatInterval = TimeSpan.FromSeconds(30);
            o.LeaseTtlSeconds = 120;
            o.WorkerDeadAfter = TimeSpan.FromSeconds(300);
        });
    }

    [Fact(DisplayName = "Two heartbeats against a claim loop blocked on a full channel reschedule none of the rows it holds")]
    public async Task Rows_behind_a_blocked_channel_write_survive_two_heartbeats()
    {
        var ct = TestContext.Current.CancellationToken;
        var (blocking, waiting) = await EnqueueAsync(ct);
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            await WaitUntilBlockedAsync(blocking, waiting, ct);

            await Runtime.RunHeartbeatOnceAsync(ct);
            await Runtime.RunHeartbeatOnceAsync(ct);

            // The release runs detached after the second tick, so a wrong release shows up a moment
            // later as a Rescheduled event on a waiting row. Nothing must appear.
            var rescheduled = await WaitUntilAsync(() => RescheduledAsync(waiting, ct), TimeSpan.FromSeconds(3), ct);
            Assert.False(rescheduled, "a row waiting behind the full channel was released as an orphaned claim");
            Assert.Equal(1, ChaosProbes.CountingInvocations[blocking.JobId]);
        }
        finally
        {
            await EndRunAsync(blocking, hostCts, run);
        }
    }

    [Fact(DisplayName = "A drain that lands on a blocked channel write hands the rows it never wrote back to Ready")]
    public async Task A_drain_hands_back_the_rows_behind_a_blocked_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var (blocking, waiting) = await EnqueueAsync(ct);
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            await WaitUntilBlockedAsync(blocking, waiting, ct);

            // The drain stops the producer mid-batch. The rows it had claimed and not written are handed
            // back at once, well inside the two heartbeats the orphan release would otherwise need.
            await Runtime.BeginDrainAsync(ct);
            var rescheduled = await WaitUntilAsync(() => RescheduledAsync(waiting, ct), TimeSpan.FromSeconds(10), ct);
            Assert.True(rescheduled, "the drain left the rows behind the blocked write leased to a worker that will not run them");

            // Every row is now in one of three states, and only the channel's three keep the lease:
            // buffered rows stay Dispatched for the executor the drain lets finish, handed-back rows
            // are Ready and unleased with the reschedule in their ledger, and the row the loop never
            // reached is Ready with no attempt at all.
            var buffered = 0;
            var handedBack = 0;
            foreach (var jobId in waiting)
            {
                var row = await Jobs.GetAsync(JobLookup.ById(jobId), ct);
                Assert.NotNull(row);
                var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId).ToListAsync(ct);
                var wasHandedBack = events.Any(e => e.ExecutionStatus == ExecutionStatusCode.Rescheduled);
                switch (row!.Status)
                {
                    case JobStatusCode.Dispatched:
                        Assert.NotNull(row.LeasedByWorkerId);
                        Assert.False(wasHandedBack, $"job {jobId} was handed back and then re-leased by a draining worker");
                        buffered++;
                        break;
                    case JobStatusCode.Ready:
                        Assert.Null(row.LeasedByWorkerId);
                        handedBack += wasHandedBack ? 1 : 0;
                        break;
                    default:
                        Assert.Fail($"job {jobId} is {row.Status} after the drain");
                        break;
                }
            }

            Assert.Equal(3, buffered);
            Assert.InRange(handedBack, 1, WaitingJobs - 3);
        }
        finally
        {
            await EndRunAsync(blocking, hostCts, run);
        }
    }

    // Everything is enqueued before the worker starts so the loop claims full batches: the holding job
    // first, then the rows that fill the channel and block the loop.
    private async Task<(JobEnqueueOutcome Blocking, List<long> Waiting)> EnqueueAsync(CancellationToken ct)
    {
        var blocking = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "chaos-holding", JobPayload.None), ct);
        ChaosProbes.Reset(blocking.JobId);
        var waiting = new List<long>(WaitingJobs);
        for (var i = 0; i < WaitingJobs; i++)
        {
            waiting.Add((await ChaosSpecHelpers.EnqueueNoPayloadAsync(Jobs, TestNamespace, "chaos-counting", ct)).JobId);
        }

        return (blocking, waiting);
    }

    // The loop is blocked once the executor is held and every row the two batches can take is
    // Dispatched under the worker's lease: the channel's three plus the ones stuck behind the write.
    private async Task WaitUntilBlockedAsync(JobEnqueueOutcome blocking, List<long> waiting, CancellationToken ct)
    {
        await ChaosProbes.WaitStartedAsync(blocking.JobId, ct).WaitAsync(SpecWaits.Gate, ct);
        var claimed = await WaitUntilAsync(async () => await DispatchedCountAsync(waiting, ct) >= WaitingJobs - 1, SpecWaits.Converge, ct);
        Assert.True(claimed, "the claim loop did not fill the channel within the converge budget");
    }

    private static async Task EndRunAsync(JobEnqueueOutcome blocking, CancellationTokenSource hostCts, Task run)
    {
        ChaosProbes.Release(blocking.JobId);
        await hostCts.CancelAsync();
        try
        {
            await run.WaitAsync(SpecWaits.Gate, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // The host token ended the run.
        }
    }

    private async Task<int> DispatchedCountAsync(List<long> jobIds, CancellationToken ct)
    {
        var dispatched = 0;
        foreach (var jobId in jobIds)
        {
            var row = await Jobs.GetAsync(JobLookup.ById(jobId), ct);
            if (row is { Status: JobStatusCode.Dispatched, LeasedByWorkerId: not null })
            {
                dispatched++;
            }
        }

        return dispatched;
    }

    private async Task<bool> RescheduledAsync(List<long> jobIds, CancellationToken ct)
    {
        foreach (var jobId in jobIds)
        {
            var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId).ToListAsync(ct);
            if (events.Any(e => e.ExecutionStatus == ExecutionStatusCode.Rescheduled))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        do
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        } while (DateTime.UtcNow < deadline);

        return false;
    }
}
