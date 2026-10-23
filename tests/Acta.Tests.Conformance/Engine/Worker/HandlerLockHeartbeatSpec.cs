using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for heartbeat renewal of handler-acquired (<c>RunWithLock</c>) locks. The worker
/// heartbeat renews every lock an attempt holds in the same call that renews its job lease, so a critical
/// section that outlives the lease TTL stays mutually exclusive; and if a held lock is lost the attempt is
/// cancelled within one heartbeat so two handlers never run the section concurrently. The aborted attempt re-arms Ready under the retry budget; the
/// row is still leased to this worker, so a terminal completion would permanently fail a live job.
/// </summary>
[ConformanceSpec(
    "handler-lock.heartbeat",
    "One heartbeat renews a handler-held lock and a lost lock cancels the attempt",
    Area = "Locks",
    Contract = "The heartbeat renews every lock an attempt holds with its job lease, and a lost held lock aborts the attempt into a retryable failure within one heartbeat."
)]
public sealed class HandlerLockHeartbeatSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // A hang guard, not a measurement: the waits below are for a probe gate and the run task, both
    // driven by acts that already happened. See SpecWaits.
    private static readonly TimeSpan Timeout = SpecWaits.Gate;

    [Fact(DisplayName = "One heartbeat advances both the attempt's job lease and its handler-held lock's lease")]
    public async Task Heartbeat_extends_a_handler_held_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        LockHolder.Reset(TestNamespace);
        var lockKey = LockKey();

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "lock-holder", JobPayload.None), ct);

        var run = Runtime.RunOnceAsync(enqueued, ct);
        await LockHolder.Entered(TestNamespace).WaitAsync(Timeout, ct);

        var before = await LeaseExpiryAsync(lockKey, ct);
        var jobLeaseBefore = (await ReadJobAsync(enqueued.JobId, ct)).LeaseExpiresAtUtc;
        // SQLite stores the expiry at millisecond precision and the acquire-to-heartbeat gap here can be
        // sub-millisecond, which would make an applied extend land on the same stored instant and read as
        // "did not advance". Let the clock tick past the stored granularity before extending.
        await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        await Runtime.RunHeartbeatOnceAsync(ct);
        var after = await LeaseExpiryAsync(lockKey, ct);

        Assert.True(after > before, $"lock lease expiry did not advance ({before:o} -> {after:o}).");
        Assert.True((await ReadJobAsync(enqueued.JobId, ct)).LeaseExpiresAtUtc > jobLeaseBefore, "job lease expiry did not advance");

        LockHolder.Release(TestNamespace);
        await run.WaitAsync(Timeout, ct);
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct))?.Status);
    }

    [Fact(DisplayName = "A lost held lock cancels the attempt within one heartbeat")]
    public async Task Lost_handler_lock_cancels_the_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        LockHolder.Reset(TestNamespace);
        var lockKey = LockKey();

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "lock-holder", JobPayload.None), ct);

        var run = Runtime.RunOnceAsync(enqueued, ct);
        await LockHolder.Entered(TestNamespace).WaitAsync(Timeout, ct);

        // Steal the lock out-of-band: delete the locks row the handler holds (token-CAS DELETE),
        // so the next heartbeat does not renew a lock the handler still relies on.
        var holdToken = await HoldTokenAsync(lockKey, ct);
        Assert.True(
            (await Services.GetRequiredService<LocksOperations>().ReleaseLockAsync(new LockToken(lockKey, holdToken), ct)).Released
        );

        await Runtime.RunHeartbeatOnceAsync(ct);

        Assert.True(await LockHolder.Observed(TestNamespace).WaitAsync(Timeout, ct));

        // The aborted attempt must NOT land terminal Failed: the row is still Executing and still
        // leased to this worker, so the completion CAS matches. The runner submits a retryable
        // attempt-aborted failure and the job re-arms.
        Assert.Equal(RunOnceStatus.Rearmed, await run.WaitAsync(Timeout, ct));
        Assert.Equal(JobStatusCode.Ready, (await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct))?.Status);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var failed = Assert.Single(events, e => e.EventCode == EventCode.JobExecutionFailed);
        Assert.Equal(EventReason.AttemptAborted, failed.Reason);
        Assert.Equal(JobStatusCode.Ready.Code, failed.To);
    }

    private string LockKey()
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        return string.Create(CultureInfo.InvariantCulture, $"{ns}.lock.{LockHolder.LockName}");
    }

    private async Task<DateTime> LeaseExpiryAsync(string lockKey, CancellationToken ct)
    {
        var row = await Db.From<Acta.LockEntity>().Where(l => l.LockKey == lockKey).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        return row!.ExpiresAtUtc;
    }

    private async Task<Guid> HoldTokenAsync(string lockKey, CancellationToken ct)
    {
        var row = await Db.From<Acta.LockEntity>().Where(l => l.LockKey == lockKey).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        return row!.HoldToken;
    }
}
