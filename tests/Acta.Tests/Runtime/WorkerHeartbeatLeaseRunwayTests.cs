using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Covers the split renew/enforce design that bounds the heartbeat's fail-open on lease-renewal exceptions. The
/// <see cref="WorkerHeartbeat"/> renews the job leases and handler-held locks in one call and only <em>feeds</em> each
/// attempt's monotonic deadlines on a confirmed renewal and cancels outright on a definitive loss; an uncertain renewal
/// (exception/timeout) feeds nothing. The <see cref="AttemptWatchdog"/> - pure in-memory, so a hung store can never
/// delay it - cancels an attempt once its earliest deadline is within the unwind margin. Deadlines are monotonic
/// Stopwatch timestamps, so tests set them relative to <see cref="Stopwatch.GetTimestamp"/> and drive the ticks
/// directly (no DB).
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

    [Fact]
    public async Task Worker_heartbeat_feeds_the_job_lease_deadline_on_a_confirmed_renewal()
    {
        var context = Context();
        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1)), context);

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
        var heartbeat = Heartbeat(WorkerStore(_ => null), context);

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
    public async Task Worker_heartbeat_keeps_a_skipped_job_running_without_feeding_its_deadline()
    {
        var context = Context();
        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1, renewed: false)), context);

        using var cts = new CancellationTokenSource();
        var deadline = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(5));
        var attempt = new RunningAttempt(cts) { JobLeaseGoodUntil = deadline };
        context.RunningAttempts[1] = attempt;

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // Another transaction held the row: it is still this worker's, but its lease did not move.
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(deadline, attempt.JobLeaseGoodUntil);
    }

    [Fact]
    public async Task Worker_heartbeat_leaves_the_deadlines_untouched_when_the_refresh_throws()
    {
        var context = Context();
        var heartbeat = Heartbeat(WorkerStore(_ => throw new InvalidOperationException("synthetic transient worker-store fault")), context);

        using var cts = new CancellationTokenSource();
        var deadline = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds));
        // Job lease seeded further out so the lock's deadline is the earliest and observable.
        var attempt = new RunningAttempt(cts) { JobLeaseGoodUntil = deadline + TicksFor(TimeSpan.FromSeconds(60)) };
        attempt.TrackLock(new LockToken("orders.flaky", Guid.NewGuid()), deadline);
        context.RunningAttempts[1] = attempt;

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // Uncertain refresh: neither cancel nor feed - the deadlines are left for the watchdog.
        Assert.False(cts.IsCancellationRequested);
        Assert.Equal(deadline, attempt.EarliestLeaseGoodUntil());
    }

    [Fact]
    public async Task Worker_heartbeat_renews_a_held_lock_in_the_same_call_and_feeds_its_deadline()
    {
        var held = new LockToken("orders.a", Guid.NewGuid());
        var context = Context();
        var session = new ScriptedSession(_ => new[] { Lease(1), Lock(held.Key) });
        var heartbeat = Heartbeat(session.Services().GetRequiredService<WorkerLifecycle>(), context);

        using var cts = new CancellationTokenSource();
        // Job lease healthy; the lock deadline starts nearly lapsed so the renewal is what pushes it back out.
        var attempt = new RunningAttempt(cts)
        {
            JobLeaseGoodUntil = Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)),
        };
        attempt.TrackLock(held, Stopwatch.GetTimestamp() - TicksFor(TimeSpan.FromSeconds(1)));
        context.RunningAttempts[1] = attempt;

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            $$"""[{"lock_key":"orders.a","hold_token":"{{held.HoldToken}}"}]""",
            Assert.Single(session.Calls).Parameters["p_locks"]
        );
        Assert.False(cts.IsCancellationRequested);
        Assert.True(attempt.EarliestLeaseGoodUntil() - Stopwatch.GetTimestamp() > TicksFor(Interval));
    }

    [Fact]
    public async Task Worker_heartbeat_cancels_when_a_held_lock_is_not_renewed()
    {
        var context = Context();
        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1)), context);

        using var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts);
        attempt.TrackLock(
            new LockToken("orders.lost", Guid.NewGuid()),
            Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds))
        );
        context.RunningAttempts[1] = attempt;

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // The hold's token moved: another holder took the key, so the critical section is no longer exclusive.
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_ignores_a_lock_released_while_the_renewal_was_in_flight()
    {
        var released = new LockToken("orders.released", Guid.NewGuid());
        var context = Context();
        using var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts);
        attempt.TrackLock(released, Stopwatch.GetTimestamp() + TicksFor(TimeSpan.FromSeconds(LeaseTtlSeconds)));
        context.RunningAttempts[1] = attempt;

        // The handler releases (untracks) the lock while the renewal is in flight, so the renewal finds no hold.
        var heartbeat = Heartbeat(
            WorkerStore(_ =>
            {
                attempt.UntrackLock(released);
                return Lease(1);
            }),
            context
        );

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(cts.IsCancellationRequested);
    }

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
        byLock.TrackLock(
            new LockToken("orders.stuck", Guid.NewGuid()),
            Stopwatch.GetTimestamp() + TicksFor(Margin) - TicksFor(TimeSpan.FromSeconds(1))
        );

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

    private static WorkerHeartbeat Heartbeat(WorkerLifecycle workers, WorkerContext context) =>
        new(
            workers,
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
            Options(),
            Registration,
            context,
            NullLogger.Instance
        );

    // The worker lifecycle over a scripted lease extend, the heartbeat's only database call.
    private static WorkerLifecycle WorkerStore(Func<BoundCall, object?> answer) =>
        new ScriptedSession(answer).Services().GetRequiredService<WorkerLifecycle>();

    private static ExtendWorkerLeasesRow Lease(long jobId, bool renewed = true) =>
        new(jobId, ExecutionNumber: 0, LockKey: null, renewed, DefinitionsModifiedAtUtc: null);

    private static ExtendWorkerLeasesRow Lock(string lockKey) =>
        new(JobId: null, ExecutionNumber: null, lockKey, Renewed: true, DefinitionsModifiedAtUtc: null);
}
