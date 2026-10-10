using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The Buffered profile against a held executor. The claimer claims only when a whole batch of permits is free, so
/// with every executor held the rows it already claimed wait in the <c>Claimed</c> channel and the rest stay Ready.
/// The heartbeat renews every row the process holds, so the waiting rows keep their leases, and a drain runs them
/// rather than dropping them: their claim committed, and a dropped row would wait out its lease.
/// </summary>
[ConformanceSpec(
    "runtime.buffered-backpressure-ownership",
    "A Buffered worker claims one batch ahead and a drain runs it",
    Area = "Runtime",
    Contract = "A Buffered worker with every executor held claims at most one batch ahead, the heartbeat renews the rows waiting for an executor, and a drain runs them."
)]
public sealed class BufferedBackpressureOwnershipSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Six claimable rows behind the holding job: one batch takes the holding job and two of them, and with the
    // only executor held no second batch fits, so four stay Ready.
    private const int WaitingJobs = 6;
    private const int BatchSize = 3;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = ExecutionProfile.Buffered;
            o.MaxConcurrentExecutors = 1;
            o.ClaimBatchSize = BatchSize;
            o.SafetyPollInterval = TimeSpan.FromSeconds(1);
            // A long beat keeps the runtime's own heartbeat out of the window; the fact ticks it by hand.
            o.HeartbeatInterval = TimeSpan.FromSeconds(30);
            o.LeaseTtlSeconds = 120;
            o.WorkerDeadAfter = TimeSpan.FromSeconds(300);
        });
    }

    [Fact(DisplayName = "A heartbeat renews the rows claimed ahead of a held executor, and no second batch is claimed")]
    public async Task Rows_claimed_ahead_are_renewed_and_no_second_batch_is_claimed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (blocking, waiting) = await EnqueueAsync(ct);
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            await WaitUntilBlockedAsync(blocking, waiting, ct);

            await AssertAllRenewedAsync(waiting, ct);
            Assert.Equal(WaitingJobs - BatchSize + 1, await CountAsync(waiting, JobStatusCode.Ready, ct));
            Assert.Equal(1, ChaosProbes.CountingInvocations[blocking.JobId]);
        }
        finally
        {
            await EndRunAsync(blocking, hostCts, run);
        }
    }

    [Fact(DisplayName = "A drain keeps renewing the rows claimed ahead and runs them once the executor frees")]
    public async Task A_drain_runs_the_rows_claimed_ahead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (blocking, waiting) = await EnqueueAsync(ct);
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);
        try
        {
            await WaitUntilBlockedAsync(blocking, waiting, ct);

            await Runtime.BeginDrainAsync(ct);
            await AssertAllRenewedAsync(waiting, ct);

            ChaosProbes.Release(blocking.JobId);
            await Runtime.DrainCompletion.WaitAsync(SpecWaits.Gate, ct);

            Assert.Equal(BatchSize - 1, await CountAsync(waiting, JobStatusCode.Succeeded, ct));
            Assert.Equal(WaitingJobs - BatchSize + 1, await CountAsync(waiting, JobStatusCode.Ready, ct));
            Assert.Equal(0, Runtime.InFlightCount);
        }
        finally
        {
            await EndRunAsync(blocking, hostCts, run);
        }
    }

    // Everything is enqueued before the worker starts so the claimer takes a full batch: the holding job
    // first, then the rows that wait behind it.
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

    // The claimer is blocked once the executor is held and the rest of the first batch is Dispatched under the
    // worker's lease, waiting for it.
    private async Task WaitUntilBlockedAsync(EnqueueJobResponse blocking, List<long> waiting, CancellationToken ct)
    {
        await ChaosProbes.WaitStartedAsync(blocking.JobId, ct).WaitAsync(SpecWaits.Gate, ct);
        var claimed = await WaitUntilAsync(
            async () => (await DispatchedLeasesAsync(waiting, ct)).Count >= BatchSize - 1,
            SpecWaits.Converge,
            ct
        );
        Assert.True(claimed, "the claimer did not claim its first batch within the converge budget");
    }

    private async Task AssertAllRenewedAsync(List<long> waiting, CancellationToken ct)
    {
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
        Assert.True(allRenewed, $"{unrenewed} rows waiting for the held executor were not renewed");
        Assert.Equal(BatchSize - 1, renewed);
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

    private async Task<int> CountAsync(List<long> jobIds, JobStatusCode status, CancellationToken ct)
    {
        var count = 0;
        foreach (var jobId in jobIds)
        {
            if ((await Jobs.GetAsync(JobLookup.ById(jobId), ct))?.Status == status)
            {
                count++;
            }
        }

        return count;
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
