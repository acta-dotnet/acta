using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The heartbeat against a Buffered claim loop that is blocked on a full channel. A claimed batch is this worker's
/// the moment the claim commits, but the loop hands the rows to the channel one at a time and a write blocks while
/// every executor is held. The heartbeat renews only what the process holds, so the rows behind the blocked write
/// must already count as buffered, or their leases would lapse while they waited for their turn. A drain that lands
/// while the write is blocked drops those same rows from the buffer, since no executor will ever take them, and
/// their leases lapse to recovery.
/// </summary>
[ConformanceSpec(
    "runtime.buffered-backpressure-ownership",
    "Rows behind a full Buffered channel stay renewed until a drain drops them",
    Area = "Runtime",
    Contract = "A row claimed but not written to a full Buffered channel is renewed by the heartbeat, and a drain stops renewing it."
)]
public sealed class BufferedBackpressureOwnershipSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
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

    [Fact(DisplayName = "A heartbeat against a claim loop blocked on a full channel renews every row it holds")]
    public async Task Rows_behind_a_blocked_channel_write_are_renewed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (blocking, waiting) = await EnqueueAsync(ct);
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            await WaitUntilBlockedAsync(blocking, waiting, ct);

            var (renewed, unrenewed) = (0, 0);
            var allRenewed = await WaitUntilAsync(
                async () =>
                {
                    (renewed, unrenewed) = await BeatAsync(waiting, ct);
                    return unrenewed == 0;
                },
                SpecWaits.Converge,
                ct
            );
            Assert.True(allRenewed, $"{unrenewed} rows waiting behind the full channel were not renewed");
            Assert.Equal(WaitingJobs - 1, renewed);
            Assert.Equal(1, ChaosProbes.CountingInvocations[blocking.JobId]);
        }
        finally
        {
            await EndRunAsync(blocking, hostCts, run);
        }
    }

    [Fact(DisplayName = "A drain that lands on a blocked channel write stops renewing the rows it never wrote")]
    public async Task A_drain_stops_renewing_the_rows_behind_a_blocked_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var (blocking, waiting) = await EnqueueAsync(ct);
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            await WaitUntilBlockedAsync(blocking, waiting, ct);

            // The drain stops the producer mid-batch: the rows it had claimed and not written leave the buffer,
            // while the channel's rows stay held for the executor the drain lets finish.
            await Runtime.BeginDrainAsync(ct);
            var (renewed, unrenewed) = (0, 0);
            var dropped = await WaitUntilAsync(
                async () =>
                {
                    (renewed, unrenewed) = await BeatAsync(waiting, ct);
                    return unrenewed > 0;
                },
                SpecWaits.Converge,
                ct
            );
            Assert.True(dropped, "the drain left the rows behind the blocked write renewed by a worker that will not run them");

            // How many rows the channel still holds depends on how far the executors got; the contract is that
            // some rows lapse and the channel's rows keep their leases.
            Assert.Equal(WaitingJobs - 1, renewed + unrenewed);
            Assert.InRange(renewed, 1, WaitingJobs - 2);
        }
        finally
        {
            await EndRunAsync(blocking, hostCts, run);
        }
    }

    // Everything is enqueued before the worker starts so the loop claims full batches: the holding job
    // first, then the rows that fill the channel and block the loop.
    private async Task<(EnqueueJobResponse Blocking, List<long> Waiting)> EnqueueAsync(CancellationToken ct)
    {
        var blocking = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "chaos-holding", JobPayload.None), ct);
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
    private async Task WaitUntilBlockedAsync(EnqueueJobResponse blocking, List<long> waiting, CancellationToken ct)
    {
        await ChaosProbes.WaitStartedAsync(blocking.JobId, ct).WaitAsync(SpecWaits.Gate, ct);
        var claimed = await WaitUntilAsync(
            async () => (await DispatchedLeasesAsync(waiting, ct)).Count >= WaitingJobs - 1,
            SpecWaits.Converge,
            ct
        );
        Assert.True(claimed, "the claim loop did not fill the channel within the converge budget");
    }

    private static async Task EndRunAsync(EnqueueJobResponse blocking, CancellationTokenSource hostCts, Task run)
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

    // One heartbeat, counting the Dispatched rows whose lease it moved and those it left where it was.
    private async Task<(int Renewed, int Unrenewed)> BeatAsync(List<long> jobIds, CancellationToken ct)
    {
        var before = await DispatchedLeasesAsync(jobIds, ct);
        // lease_expires_at_utc is DATETIME2(3) on SQL Server: the beat must land in a later millisecond to move it.
        await Task.Delay(5, ct);
        await Runtime.RunHeartbeatOnceAsync(ct);
        var after = await DispatchedLeasesAsync(jobIds, ct);
        var renewed = before.Count(lease => after.TryGetValue(lease.Key, out var now) && now > lease.Value);
        return (renewed, before.Count - renewed);
    }

    private async Task<Dictionary<long, DateTime>> DispatchedLeasesAsync(List<long> jobIds, CancellationToken ct)
    {
        var leases = new Dictionary<long, DateTime>();
        foreach (var jobId in jobIds)
        {
            var row = await Jobs.GetAsync(JobLookup.ById(jobId), ct);
            if (row is { Status: JobStatusCode.Dispatched, LeaseExpiresAtUtc: { } lease })
            {
                leases[jobId] = lease;
            }
        }

        return leases;
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
