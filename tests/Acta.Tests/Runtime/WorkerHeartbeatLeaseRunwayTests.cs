using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// What one heartbeat tick does to an attempt's lease deadline. The <see cref="WorkerHeartbeat"/> renews the job leases
/// and handler-held locks in one call, re-arms each attempt's deadline on a confirmed renewal, and cancels outright on a
/// definitive loss; a call that throws moves nothing, so an outage cancels by the deadline alone. The deadlines are
/// real timers, so these facts arm them short and wait on the attempt token (no DB).
/// </summary>
public sealed class WorkerHeartbeatLeaseRunwayTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);
    private const int LeaseTtlSeconds = 180;

    // Short enough to fire within a fact, long enough that a scripted tick finishes well before it.
    private static readonly TimeSpan ShortLease = TimeSpan.FromMilliseconds(300);

    // How early a timer may fire against the Stopwatch: the timer queue counts in coarse milliseconds.
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    // A hang guard, not a measurement: only a deadline that never fires reaches it.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static IOptions<JobsOptions> Options(TimeSpan interval, int leaseTtlSeconds) =>
        Microsoft.Extensions.Options.Options.Create(new JobsOptions { HeartbeatInterval = interval, LeaseTtlSeconds = leaseTtlSeconds });

    private static WorkerContext Context()
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        return context;
    }

    private static readonly WorkerRegistration Registration = new("orders", null, null, [], []);

    [Fact]
    public async Task A_confirmed_renewal_re_arms_the_lease_deadline()
    {
        var context = Context();
        using var attempt = Leased(ShortLease);
        context.RunningAttempts[1] = attempt;

        await Heartbeat(WorkerStore(_ => Lease(1)), context).TickAsync(TestContext.Current.CancellationToken);
        await Task.Delay(ShortLease * 2, TestContext.Current.CancellationToken);

        // The renewal moved the deadline to the runway past its request start, minutes away.
        Assert.False(attempt.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_lease_lapse_cancels_the_attempt_the_margin_before_the_lease_expires()
    {
        // A one-second lease on a 10ms beat: the margin is (1000 - 10) / 2 = 495ms, so the renewed deadline falls 505ms
        // past the renewal's request start, and the attempt is cancelled well before the lease itself lapses at one
        // second. It also outlives its first deadline, which the renewal replaced.
        var options = Options(TimeSpan.FromMilliseconds(10), leaseTtlSeconds: 1);
        var context = Context();
        using var attempt = Leased(ShortLease);
        context.RunningAttempts[1] = attempt;
        var renewedAt = Stopwatch.GetTimestamp();

        await Heartbeat(WorkerStore(_ => Lease(1)), context, options).TickAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, attempt.AttemptToken));

        Assert.InRange(
            Stopwatch.GetElapsedTime(renewedAt),
            RunningAttempt.LeaseRunway(options.Value) - TimerSlack,
            TimeSpan.FromSeconds(1)
        );
    }

    [Fact]
    public async Task An_authoritative_refresh_that_drops_the_execution_cancels_at_once()
    {
        var context = Context();
        using var attempt = new RunningAttempt(leased: false, CancellationToken.None);
        context.RunningAttempts[1] = attempt;

        await Heartbeat(WorkerStore(_ => null), context).TickAsync(TestContext.Current.CancellationToken);

        // The job is absent from an authoritative live set (operator cancel / reclaimed lease) -> stop now.
        Assert.True(attempt.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_skipped_row_keeps_running_on_its_unmoved_deadline()
    {
        var context = Context();
        using var attempt = Leased(ShortLease);
        context.RunningAttempts[1] = attempt;
        var armedAt = Stopwatch.GetTimestamp();

        await Heartbeat(WorkerStore(_ => Lease(1, renewed: false)), context).TickAsync(TestContext.Current.CancellationToken);

        // Another transaction held the row: it is still this worker's, so the tick leaves it running, but its lease
        // did not move, so neither does its deadline.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, attempt.AttemptToken));
        Assert.True(Stopwatch.GetElapsedTime(armedAt) >= ShortLease - TimerSlack);
    }

    [Fact]
    public async Task A_refresh_that_throws_moves_nothing_so_an_outage_cancels_by_the_deadline_alone()
    {
        var context = Context();
        using var attempt = Leased(ShortLease);
        attempt.TrackLock(new LockToken("orders.flaky", Guid.NewGuid()));
        context.RunningAttempts[1] = attempt;
        var armedAt = Stopwatch.GetTimestamp();

        await Heartbeat(WorkerStore(_ => throw new InvalidOperationException("synthetic transient worker-store fault")), context)
            .TickAsync(TestContext.Current.CancellationToken);

        // Neither the job nor its unrenewed lock counts as lost on a call that threw.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, attempt.AttemptToken));
        Assert.True(Stopwatch.GetElapsedTime(armedAt) >= ShortLease - TimerSlack);
    }

    [Fact]
    public async Task Worker_heartbeat_renews_a_held_lock_in_the_same_call()
    {
        var held = new LockToken("orders.a", Guid.NewGuid());
        var context = Context();
        var session = new ScriptedSession(_ => new[] { Lease(1), Lock(held.Key) });
        using var attempt = new RunningAttempt(leased: false, CancellationToken.None);
        attempt.TrackLock(held);
        context.RunningAttempts[1] = attempt;

        await Heartbeat(session.Services().GetRequiredService<WorkerLifecycle>(), context).TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            $$"""[{"lock_key":"orders.a","hold_token":"{{held.HoldToken}}"}]""",
            Assert.Single(session.Calls).Parameters["p_locks"]
        );
        Assert.False(attempt.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_cancels_when_a_held_lock_is_not_renewed()
    {
        var context = Context();
        using var attempt = new RunningAttempt(leased: false, CancellationToken.None);
        attempt.TrackLock(new LockToken("orders.lost", Guid.NewGuid()));
        context.RunningAttempts[1] = attempt;

        await Heartbeat(WorkerStore(_ => Lease(1)), context).TickAsync(TestContext.Current.CancellationToken);

        // The hold's token moved: another holder took the key, so the critical section is no longer exclusive.
        Assert.True(attempt.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_ignores_a_lock_released_while_the_renewal_was_in_flight()
    {
        var released = new LockToken("orders.released", Guid.NewGuid());
        var context = Context();
        using var attempt = new RunningAttempt(leased: false, CancellationToken.None);
        attempt.TrackLock(released);
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

        Assert.False(attempt.AttemptToken.IsCancellationRequested);
    }

    private static RunningAttempt Leased(TimeSpan lease)
    {
        var attempt = new RunningAttempt(leased: true, CancellationToken.None);
        attempt.ArmLease(lease);
        return attempt;
    }

    private static WorkerHeartbeat Heartbeat(WorkerLifecycle workers, WorkerContext context, IOptions<JobsOptions>? options = null) =>
        new(
            workers,
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
            options ?? Options(Interval, LeaseTtlSeconds),
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
