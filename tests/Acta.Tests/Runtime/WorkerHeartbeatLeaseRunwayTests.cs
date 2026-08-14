using System.Diagnostics;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Locks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Covers the split renew/enforce design that bounds the heartbeat's fail-open on lease-renewal exceptions.
/// Renewers (<see cref="WorkerHeartbeat"/> for the batched worker/job lease, <see cref="LockLeaseHeartbeat"/>
/// for handler-held locks) only <em>feed</em> each attempt's monotonic deadline on a confirmed renewal and
/// cancel outright on a definitive loss; an uncertain renewal (exception/timeout) feeds nothing. The
/// <see cref="AttemptWatchdog"/> - pure in-memory, so a hung store can never delay it - cancels an attempt
/// once its earliest deadline is within the unwind margin. Deadlines are monotonic Stopwatch timestamps, so
/// tests set them relative to <see cref="Stopwatch.GetTimestamp"/> and drive the ticks directly (no DB).
/// </summary>
public sealed class WorkerHeartbeatLeaseRunwayTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);
    private const int LeaseTtlSeconds = 180;

    // Margin the watchdog cancels within: half the base runway (TTL - interval)/2, matching AttemptWatchdog.
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds((LeaseTtlSeconds - 45) / 2.0);

    private static long TicksFor(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    private static IOptions<JobsOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new JobsOptions { HeartbeatInterval = Interval, LeaseTtlSeconds = LeaseTtlSeconds });

    private static WorkerContext Context()
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        return context;
    }

    private static readonly WorkerRegistration Registration = new("orders", null, null, [], []);

    // ---- WorkerHeartbeat: feeds the job-lease deadline; cancels only on an authoritative drop ----

    [Fact]
    public async Task Worker_heartbeat_feeds_the_job_lease_deadline_on_a_confirmed_renewal()
    {
        var context = Context();
        var heartbeat = new WorkerHeartbeat(new LiveWorkerStore([1]), Options(), Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts) { JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(5)) };
        context.RunningAttempts[1] = attempt;

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // A confirmed renewal pushed the deadline out to ~now + TTL, so the runway is full again.
        Assert.False(cts.IsCancellationRequested);
        Assert.True(attempt.EarliestLeaseGoodUntil() - Stopwatch.GetTimestamp() > TicksFor(Interval));
    }

    [Fact]
    public async Task Worker_heartbeat_cancels_immediately_when_an_authoritative_refresh_drops_the_job()
    {
        var context = Context();
        var heartbeat = new WorkerHeartbeat(new LiveWorkerStore([]), Options(), Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts)
        {
            JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)),
        };
        context.RunningAttempts[1] = attempt;

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // The job is absent from an authoritative live set (operator cancel / reclaimed lease) -> stop now.
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_leaves_the_deadline_untouched_when_the_refresh_throws()
    {
        var context = Context();
        var heartbeat = new WorkerHeartbeat(new ThrowingWorkerStore(), Options(), Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var deadline = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds));
        var attempt = new RunningAttempt(cts) { JobLeaseGoodUntil = deadline };
        context.RunningAttempts[1] = attempt;

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // Uncertain refresh: neither cancel nor feed - the deadline is left for the watchdog.
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(deadline, attempt.JobLeaseGoodUntil);
    }

    // ---- LockLeaseHeartbeat: feeds lock deadlines; cancels on false; tolerates throw/release-race ----

    [Fact]
    public async Task Lock_heartbeat_extends_held_locks_even_when_the_worker_lease_is_failing()
    {
        // The worker-lease store being down must not stop the (independent) lock store from renewing.
        var lockA = new LockToken("orders.a", 1);
        var lockB = new LockToken("orders.b", 1);
        var lockStore = new RecordingLockStore(_ => true);
        var context = Context();
        var lockHeartbeat = new LockLeaseHeartbeat(lockStore, Options(), Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        // Job lease healthy (seeded far out); the lock deadlines start nearly lapsed so a successful extend
        // is what pushes the earliest deadline back out.
        var attempt = new RunningAttempt(cts)
        {
            JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)),
        };
        attempt.TrackLock(lockA, Stopwatch.GetTimestamp() - TicksFor(TimeSpan.FromSeconds(1)));
        attempt.TrackLock(lockB, Stopwatch.GetTimestamp() - TicksFor(TimeSpan.FromSeconds(1)));
        context.RunningAttempts[1] = attempt;

        await lockHeartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested);
        Assert.Contains(lockA, lockStore.Extended);
        Assert.Contains(lockB, lockStore.Extended);
        // Both deadlines advanced to ~now + TTL, so the attempt has a full runway again.
        Assert.True(attempt.EarliestLeaseGoodUntil() - Stopwatch.GetTimestamp() > TicksFor(Interval));
    }

    [Fact]
    public async Task Lock_heartbeat_cancels_immediately_when_a_held_lock_is_lost()
    {
        var lost = new LockToken("orders.lost", 1);
        var lockStore = new RecordingLockStore(_ => false);
        var context = Context();
        var lockHeartbeat = new LockLeaseHeartbeat(lockStore, Options(), Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts);
        attempt.TrackLock(lost, Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)));
        context.RunningAttempts[1] = attempt;

        await lockHeartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(cts.IsCancellationRequested);
        Assert.Single(lockStore.Extended, lost);
    }

    [Fact]
    public async Task Lock_heartbeat_tolerates_a_transient_throw_without_cancelling_or_feeding()
    {
        var flaky = new LockToken("orders.flaky", 1);
        var lockStore = new ThrowingLockStore(flaky);
        var context = Context();
        var lockHeartbeat = new LockLeaseHeartbeat(lockStore, Options(), Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var deadline = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds));
        // Job lease seeded further out so the flaky lock's deadline is the earliest and observable.
        var attempt = new RunningAttempt(cts) { JobLeaseGoodUntil = deadline + TicksFor(TimeSpan.FromSeconds(60)) };
        attempt.TrackLock(flaky, deadline);
        context.RunningAttempts[1] = attempt;

        await lockHeartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(deadline, attempt.EarliestLeaseGoodUntil()); // not fed
    }

    [Fact]
    public async Task Lock_heartbeat_ignores_a_throw_for_a_lock_released_mid_extend()
    {
        // Simulate the handler releasing (untracking) the lock while the extend is in flight, then the
        // extend throwing: the renewer must treat it as a non-failure, not cancel.
        var released = new LockToken("orders.released", 1);
        var context = Context();
        using var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts);
        attempt.TrackLock(released, Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)));
        context.RunningAttempts[1] = attempt;

        var lockStore = new ThrowingLockStore(released, onExtend: () => attempt.UntrackLock(released));
        var lockHeartbeat = new LockLeaseHeartbeat(lockStore, Options(), Registration, context, NullLogger.Instance);

        await lockHeartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested);
    }

    // ---- AttemptWatchdog: enforces deadlines, in-memory, per attempt ----

    [Fact]
    public Task Watchdog_does_not_cancel_an_attempt_with_runway_beyond_the_margin()
    {
        var context = Context();
        var watchdog = new AttemptWatchdog(Options(), Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts)
        {
            JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)),
        };
        context.RunningAttempts[1] = attempt;

        watchdog.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested);
        return Task.CompletedTask;
    }

    [Fact]
    public Task Watchdog_cancels_an_attempt_whose_earliest_deadline_is_within_the_margin()
    {
        var context = Context();
        var watchdog = new AttemptWatchdog(Options(), Registration, context, NullLogger.Instance);

        using var jobCts = new CancellationTokenSource();
        using var lockCts = new CancellationTokenSource();

        // (a) job lease about to lapse.
        var byJob = new RunningAttempt(jobCts)
        {
            JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(Margin) - TicksFor(TimeSpan.FromSeconds(1)),
        };
        // (b) job lease fine, but a held lock is about to lapse -> min governs.
        var byLock = new RunningAttempt(lockCts)
        {
            JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)),
        };
        byLock.TrackLock(new LockToken("orders.stuck", 1), Stopwatch.GetTimestamp() + TicksFor(Margin) - TicksFor(TimeSpan.FromSeconds(1)));

        context.RunningAttempts[1] = byJob;
        context.RunningAttempts[2] = byLock;

        watchdog.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(jobCts.IsCancellationRequested);
        Assert.True(lockCts.IsCancellationRequested);
        return Task.CompletedTask;
    }

    [Fact]
    public Task Watchdog_does_not_cancel_a_healthy_attempt_at_the_tightest_2x_config()
    {
        // Regression: the margin must stay below the runway a healthy attempt still has just before each
        // renewal, even at the validator's floor (LeaseTtl = 2x HeartbeatInterval). There the pre-renewal
        // runway dips to one interval; a fixed 2x-interval margin would cancel a perfectly healthy job.
        const int tightTtl = 90; // 2x the 45s interval
        var options = Microsoft.Extensions.Options.Options.Create(
            new JobsOptions { HeartbeatInterval = Interval, LeaseTtlSeconds = tightTtl }
        );
        var context = Context();
        var watchdog = new AttemptWatchdog(options, Registration, context, NullLogger.Instance);

        using var cts = new CancellationTokenSource();
        // Deadline at the healthy pre-renewal dip: TTL - interval = one interval of runway remaining.
        var attempt = new RunningAttempt(cts) { JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(Interval) };
        context.RunningAttempts[1] = attempt;

        watchdog.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested);
        return Task.CompletedTask;
    }

    [Fact]
    public Task Watchdog_worst_scan_phase_preserves_quarter_interval_runway_at_2x_floor()
    {
        const int tightTtl = 90;
        var options = Microsoft.Extensions.Options.Options.Create(
            new JobsOptions { HeartbeatInterval = Interval, LeaseTtlSeconds = tightTtl }
        );
        var context = Context();
        var margin = TimeSpan.FromSeconds((tightTtl - Interval.TotalSeconds) / 2.0);
        var cadence = TimeSpan.FromTicks(Interval.Ticks / 4);
        var deadline = TicksFor(TimeSpan.FromSeconds(100));
        var threshold = deadline - TicksFor(margin);
        var now = threshold - 1; // scan immediately before the cancellation threshold is crossed
        var watchdog = new AttemptWatchdog(options, Registration, context, NullLogger.Instance, () => now);

        using var cts = new CancellationTokenSource();
        context.RunningAttempts[1] = new RunningAttempt(cts) { JobLeaseGoodUntil = deadline };

        watchdog.TickAsync(TestContext.Current.CancellationToken);
        Assert.False(cts.IsCancellationRequested);

        // The next on-cadence scan is the worst timer phase. It must cancel with at least I/4 left.
        now += TicksFor(cadence);
        watchdog.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(cts.IsCancellationRequested);
        Assert.True(deadline - now >= TicksFor(cadence));
        return Task.CompletedTask;
    }

    // ---- test doubles ----

    private abstract class WorkerStoreStub : IWorkerStore
    {
        public abstract Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(
            int workerId,
            int leaseTtlSeconds,
            bool draining,
            CancellationToken ct
        );

        public Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task StopWorkerAsync(short namespaceId, int workerId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<WorkerDetail?> GetWorkerAsync(int workerId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class LiveWorkerStore(IReadOnlyList<long> liveJobIds) : WorkerStoreStub
    {
        public override Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(
            int workerId,
            int leaseTtlSeconds,
            bool draining,
            CancellationToken ct
        ) => Task.FromResult(liveJobIds);
    }

    private sealed class ThrowingWorkerStore : WorkerStoreStub
    {
        public override Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(
            int workerId,
            int leaseTtlSeconds,
            bool draining,
            CancellationToken ct
        ) => throw new InvalidOperationException("synthetic transient worker-store fault");
    }

    private sealed class RecordingLockStore(Func<LockToken, bool> result) : ILockStore
    {
        private readonly List<LockToken> _extended = [];

        public IReadOnlyList<LockToken> Extended => _extended;

        public Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct)
        {
            _extended.Add(token);
            return Task.FromResult(result(token));
        }

        public Task<bool> ReleaseAsync(LockToken token, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ThrowingLockStore(LockToken flaky, Action? onExtend = null) : ILockStore
    {
        public Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct)
        {
            if (token == flaky)
            {
                onExtend?.Invoke();
                return Task.FromException<bool>(new InvalidOperationException("synthetic transient lock-store fault"));
            }
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseAsync(LockToken token, CancellationToken ct) => throw new NotSupportedException();
    }
}
