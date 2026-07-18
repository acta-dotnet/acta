using System.Globalization;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Services.Locks;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for heartbeat renewal of handler-acquired (<c>RunWithLock</c>) locks. The worker
/// heartbeat must extend every lock an attempt holds, so a critical section that outlives the lease
/// TTL stays mutually exclusive; and if a held lock is lost the attempt is cancelled so two handlers
/// never run the section concurrently.
/// </summary>
[ConformanceSpec(
    "handler-lock.heartbeat",
    "Heartbeat extends a handler-held lock and a lost lock cancels the attempt",
    Area = "Locks",
    Contract = "The heartbeat extends every lock an attempt holds so a long critical section stays exclusive, and a lost held lock cancels the attempt.",
    Arrange = "A lock-holder handler that holds a RunWithLock lock through a long critical section is registered.",
    Act = "One run holds the lock across heartbeat ticks, and in a second run the held lock is deleted out-of-band.",
    Assert = "The heartbeat advances the held lock's lease expiry, and the lost lock cancels the attempt on the next tick."
)]
public abstract class HandlerLockHeartbeatSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact(DisplayName = "Heartbeat advances a handler-held lock's lease")]
    public async Task Heartbeat_extends_a_handler_held_lock()
    {
        var ct = TestContext.Current.CancellationToken;
        LockHolder.Reset(TestNamespace);
        var lockKey = LockKey();

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "lock-holder", JobPayload.None), ct);

        var run = Runtime.RunOnceAsync(enqueued, ct);
        await LockHolder.Entered(TestNamespace).WaitAsync(Timeout, ct);

        var before = await LeaseExpiryAsync(lockKey, ct);
        await Runtime.RunHeartbeatOnceAsync(ct);
        var after = await LeaseExpiryAsync(lockKey, ct);

        // Before the fix the heartbeat never touched a handler lock, so the expiry would be unchanged.
        Assert.True(after > before, $"lock lease expiry did not advance ({before:o} -> {after:o}).");

        LockHolder.Release(TestNamespace);
        await run.WaitAsync(Timeout, ct);
        Assert.Equal(JobStatusCode.Done, await Jobs.GetStatusAsync(JobLookup.ById(enqueued.JobId), ct));
    }

    [Fact(DisplayName = "A lost held lock cancels the attempt")]
    public async Task Lost_handler_lock_cancels_the_attempt()
    {
        var ct = TestContext.Current.CancellationToken;
        LockHolder.Reset(TestNamespace);
        var lockKey = LockKey();

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "lock-holder", JobPayload.None), ct);

        var run = Runtime.RunOnceAsync(enqueued, ct);
        await LockHolder.Entered(TestNamespace).WaitAsync(Timeout, ct);

        // Steal the lock out-of-band: delete the leases row the handler holds (version-CAS DELETE),
        // so the next heartbeat extend fails for a lock the handler still relies on.
        var version = await LeaseVersionAsync(lockKey, ct);
        Assert.True(await Services.GetRequiredService<ILockStore>().ReleaseAsync(new LockToken(lockKey, version), ct));

        await Runtime.RunHeartbeatOnceAsync(ct);

        Assert.True(await LockHolder.Observed(TestNamespace).WaitAsync(Timeout, ct));
        await run.WaitAsync(Timeout, ct);
    }

    private string LockKey()
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        return string.Create(CultureInfo.InvariantCulture, $"{ns}.lock.{LockHolder.LockName}");
    }

    private async Task<DateTime> LeaseExpiryAsync(string lockKey, CancellationToken ct)
    {
        var row = await Db.From<Lease>().Where(l => l.LeaseKey == lockKey).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        return row!.ExpiresAtUtc;
    }

    private async Task<int> LeaseVersionAsync(string lockKey, CancellationToken ct)
    {
        var row = await Db.From<Lease>().Where(l => l.LeaseKey == lockKey).SingleOrDefaultAsync(ct);
        Assert.NotNull(row);
        return row!.Version;
    }
}
