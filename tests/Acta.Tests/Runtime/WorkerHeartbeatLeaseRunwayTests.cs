using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// What one heartbeat tick does to an entry's lease deadline. The <see cref="WorkerHeartbeat"/> renews the held
/// executions and handler-held locks in one call, re-arms each entry's deadline on a confirmed renewal, and cancels
/// outright on a definitive loss; a call that throws moves nothing, so an outage cancels by the deadline alone. The
/// deadlines are real timers: a fact about a deadline firing arms it short and waits on the attempt token, and a fact
/// about a renewal reads the headroom the renewal left (no DB).
/// </summary>
public sealed class WorkerHeartbeatLeaseRunwayTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(45);
    private const int LeaseTtlSeconds = 180;

    /// <summary>
    /// Short enough to fire within a fact, for the facts whose subject is the claim's own deadline firing.
    /// </summary>
    private static readonly TimeSpan ShortLease = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// A claim's deadline that no fact outlives, so only a renewal moves it within one. It sits under the runway a
    /// renewal arms with this file's TTL and interval, so headroom above it proves the re-arm.
    /// </summary>
    private static readonly TimeSpan ClaimRunway = TimeSpan.FromSeconds(30);

    // How early a timer may fire against the Stopwatch: the timer queue counts in coarse milliseconds.
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    // A hang guard, not a measurement: only a deadline that never fires reaches it.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static IOptions<JobsOptions> Options(TimeSpan interval, int leaseTtlSeconds) =>
        Microsoft.Extensions.Options.Options.Create(new JobsOptions { HeartbeatInterval = interval, LeaseTtlSeconds = leaseTtlSeconds });

    private static readonly WorkerRegistration Registration = new("orders", null, null, [], []);

    private readonly InFlightJobRegistry _registry = new(new JobsOptions());

    [Fact]
    public async Task A_confirmed_renewal_re_arms_the_lease_deadline()
    {
        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1)));
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway));

        Assert.True(await heartbeat.TickAsync(TestContext.Current.CancellationToken));

        // The renewal moved the deadline to the runway past its request start, minutes away.
        Assert.True(entry.LeaseHeadroom > ClaimRunway, $"the deadline stood {entry.LeaseHeadroom} out after the renewal.");
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_confirmed_renewal_moves_the_cancellation_timer_past_the_claim_deadline()
    {
        // The renewal re-arms the timer itself, not only the deadline it records: the attempt outlives the claim's short
        // deadline. A trial whose tick ended past that deadline proves nothing, since the claim's timer may rightly have
        // fired first, so it runs again on a fresh entry.
        var trials = Stopwatch.StartNew();
        for (var jobId = 100L; ; jobId++)
        {
            Assert.True(trials.Elapsed < HangGuard, "no renewal landed inside the claim's deadline");
            var id = jobId;
            var heartbeat = Heartbeat(WorkerStore(_ => Lease(id)));
            var armedAt = Stopwatch.GetTimestamp();
            using var entry = Held(Entry(id, leaseRunway: ShortLease));
            Assert.True(await heartbeat.TickAsync(TestContext.Current.CancellationToken));
            if (Stopwatch.GetElapsedTime(armedAt) >= ShortLease)
            {
                _registry.Remove(entry);
                continue;
            }

            await Task.Delay(ShortLease * 2, TestContext.Current.CancellationToken);
            Assert.False(entry.AttemptToken.IsCancellationRequested);
            return;
        }
    }

    [Fact]
    public async Task A_confirmed_renewal_reports_the_headroom_the_attempt_had_left()
    {
        using var metrics = new ActaMetrics();
        var headroomMs = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter) && instrument.Name == "acta.lease.headroom")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => headroomMs.Add(value));
        listener.Start();

        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1)), metrics: metrics);
        var armedAt = Stopwatch.GetTimestamp();
        using var entry = Held(Entry(1, leaseRunway: TimeSpan.FromMinutes(1)));

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);
        var armedFor = Stopwatch.GetElapsedTime(armedAt);

        // The deadline armed at the claim stood a minute out, less the time since, when the renewal confirmed it.
        Assert.InRange(Assert.Single(headroomMs), (TimeSpan.FromMinutes(1) - armedFor).TotalMilliseconds - 1, 60_000);
    }

    [Fact]
    public async Task A_lease_lapse_cancels_the_attempt_the_margin_before_the_lease_expires()
    {
        // A one-second lease on a 10ms beat: the margin is (1000 - 10) / 2 = 495ms, so the renewed deadline falls 505ms
        // past the renewal's request start, and the attempt is cancelled well before the lease itself lapses at one
        // second. The renewal replaces the claim's farther deadline, so the attempt ends on the renewed one.
        var options = Options(TimeSpan.FromMilliseconds(10), leaseTtlSeconds: 1);
        var runway = InFlightJob.LeaseRunway(options.Value);
        Assert.True(runway < TimeSpan.FromSeconds(1));
        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1)), options);
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway));

        var renewedAt = Stopwatch.GetTimestamp();
        await heartbeat.TickAsync(TestContext.Current.CancellationToken);
        var headroom = Assert.NotNull(entry.LeaseHeadroom);
        var ticked = Stopwatch.GetElapsedTime(renewedAt);

        // The heartbeat arms the runway from its request start, which falls between renewedAt and the headroom read.
        Assert.InRange(headroom, runway - ticked - TimeSpan.FromMilliseconds(1), runway);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, entry.AttemptToken));
        // Well inside the claim's deadline, which an unmoved timer would have waited out.
        Assert.InRange(Stopwatch.GetElapsedTime(renewedAt), runway - TimerSlack, ClaimRunway / 2);
    }

    [Fact]
    public async Task An_authoritative_refresh_that_drops_the_execution_cancels_all_but_a_finalizing_entry()
    {
        var heartbeat = Heartbeat(WorkerStore(_ => null));
        using var claimed = Held(Entry(1));
        using var executing = Held(Entry(2, state: InFlightJobState.Executing));
        using var finalizing = Held(Entry(3, state: InFlightJobState.Finalizing));

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // The rows are absent from an authoritative answer (operator cancel / reclaimed lease): a claim or a running
        // handler stops now, while a finished attempt's completion write is left to its compare-and-swap.
        Assert.True(claimed.AttemptToken.IsCancellationRequested);
        Assert.True(executing.AttemptToken.IsCancellationRequested);
        Assert.False(finalizing.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_finalizing_entry_is_held_and_renewed()
    {
        var session = new ScriptedSession(_ => Lease(1));
        var heartbeat = Heartbeat(session.Services().GetRequiredService<WorkersOperations>());
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway, state: InFlightJobState.Finalizing));

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal("""[{"job_id":1,"execution_number":0}]""", Assert.Single(session.Calls).Parameters["p_executions"]);
        Assert.True(entry.LeaseHeadroom > ClaimRunway, $"the deadline stood {entry.LeaseHeadroom} out after the renewal.");
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_skipped_row_keeps_running_on_its_unmoved_deadline()
    {
        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1, renewed: false)));
        using var entry = Held(Entry(1, leaseRunway: ShortLease));
        var armedAt = Stopwatch.GetTimestamp();

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // Another transaction held the row: it is still this worker's, so the tick leaves it running, but its lease
        // did not move, so neither does its deadline.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, entry.AttemptToken));
        Assert.True(Stopwatch.GetElapsedTime(armedAt) >= ShortLease - TimerSlack);
    }

    [Fact]
    public async Task A_skipped_row_is_asked_for_again_within_the_beat_and_re_armed_once_renewed()
    {
        var calls = 0;
        var session = new ScriptedSession(_ => Lease(1, renewed: Interlocked.Increment(ref calls) > 1));
        var heartbeat = Heartbeat(session.Services().GetRequiredService<WorkersOperations>());
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway));

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // A handler write held the row through the first ask; the second ask, for that row alone, renewed it.
        Assert.Equal(2, session.Calls.Count);
        Assert.Equal("""[{"job_id":1,"execution_number":0}]""", session.Calls[1].Parameters["p_executions"]);
        Assert.True(entry.LeaseHeadroom > ClaimRunway, $"the deadline stood {entry.LeaseHeadroom} out after the renewal.");
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_refresh_that_throws_moves_nothing_so_an_outage_cancels_by_the_deadline_alone()
    {
        var heartbeat = Heartbeat(WorkerStore(_ => throw new InvalidOperationException("synthetic transient worker-store fault")));
        using var entry = Held(Entry(1, leaseRunway: ShortLease));
        var armedAt = Stopwatch.GetTimestamp();
        entry.TrackLock(new LockToken("orders.flaky", Guid.NewGuid()), ClaimRunway);

        // The pass reports the renewal unanswered, which is what a spec repeating a beat until one lands reads.
        Assert.False(await heartbeat.TickAsync(TestContext.Current.CancellationToken));

        // Neither the job nor its unrenewed lock counts as lost on a call that threw.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, entry.AttemptToken));
        Assert.True(Stopwatch.GetElapsedTime(armedAt) >= ShortLease - TimerSlack);
    }

    [Fact]
    public async Task Worker_heartbeat_renews_a_held_lock_in_the_same_call()
    {
        var held = new LockToken("orders.a", Guid.NewGuid());
        var session = new ScriptedSession(_ => new[] { Lease(1), Lock(held) });
        var heartbeat = Heartbeat(session.Services().GetRequiredService<WorkersOperations>());
        using var entry = Held(Entry(1));
        entry.TrackLock(held, ClaimRunway);

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            $$"""[{"lock_key":"orders.a","hold_token":"{{held.HoldToken}}"}]""",
            Assert.Single(session.Calls).Parameters["p_locks"]
        );
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_cancels_when_a_held_lock_is_not_renewed()
    {
        var heartbeat = Heartbeat(WorkerStore(_ => Lease(1)));
        using var entry = Held(Entry(1));
        entry.TrackLock(new LockToken("orders.lost", Guid.NewGuid()), ClaimRunway);

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        // The hold's token moved: another holder took the key, so the critical section is no longer exclusive.
        Assert.True(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_stale_hold_on_a_key_another_attempt_of_this_process_holds_is_lost()
    {
        // Attempt 1's hold lapsed and attempt 2, on the same process, took the key. The renewal finds only attempt 2's
        // token, and answering by token, not by key, tells the two holds apart.
        var stale = new LockToken("orders.k", Guid.NewGuid());
        var current = new LockToken("orders.k", Guid.NewGuid());
        var heartbeat = Heartbeat(WorkerStore(_ => new[] { Lease(1), Lease(2), Lock(current) }));
        using var first = Held(Entry(1));
        using var second = Held(Entry(2));
        first.TrackLock(stale, ClaimRunway);
        second.TrackLock(current, ClaimRunway);

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(first.AttemptToken.IsCancellationRequested);
        Assert.False(second.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public void A_hold_answered_after_its_runway_cancels_the_attempt()
    {
        // The acquire committed, then its answer took longer than the runway: the hold may have lapsed already.
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway));

        entry.TrackLock(new LockToken("orders.late", Guid.NewGuid()), TimeSpan.FromSeconds(-1));

        Assert.True(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public void A_hold_answered_late_brings_the_deadline_forward_and_its_release_moves_it_back()
    {
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway));
        var hold = new LockToken("orders.late", Guid.NewGuid());

        entry.TrackLock(hold, TimeSpan.FromSeconds(1));
        Assert.True(entry.LeaseHeadroom <= TimeSpan.FromSeconds(1), $"the deadline stood {entry.LeaseHeadroom} out with the hold.");

        entry.UntrackLock(hold);
        Assert.True(entry.LeaseHeadroom > TimeSpan.FromSeconds(1), $"the deadline stood {entry.LeaseHeadroom} out after the release.");
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_renewal_moves_a_renewed_hold_with_the_lease()
    {
        var hold = new LockToken("orders.k", Guid.NewGuid());
        var heartbeat = Heartbeat(WorkerStore(_ => new[] { Lease(1), Lock(hold) }));
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway));
        entry.TrackLock(hold, TimeSpan.FromSeconds(1));

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(entry.LeaseHeadroom > ClaimRunway, $"the deadline stood {entry.LeaseHeadroom} out after the renewal.");
    }

    [Fact]
    public async Task A_hold_taken_while_the_renewal_was_in_flight_keeps_its_own_deadline()
    {
        // The hold was not in the beat's snapshot, so the beat did not renew it, and renewing the lease must not carry
        // the attempt past the hold's own runway.
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway));
        var heartbeat = Heartbeat(
            WorkerStore(_ =>
            {
                entry.TrackLock(new LockToken("orders.mid", Guid.NewGuid()), TimeSpan.FromSeconds(1));
                return Lease(1);
            })
        );

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(entry.LeaseHeadroom <= TimeSpan.FromSeconds(1), $"the deadline stood {entry.LeaseHeadroom} out after the renewal.");
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_ignores_a_lock_released_while_the_renewal_was_in_flight()
    {
        var released = new LockToken("orders.released", Guid.NewGuid());
        using var entry = Held(Entry(1));
        entry.TrackLock(released, ClaimRunway);

        // The handler releases (untracks) the lock while the renewal is in flight, so the renewal finds no hold.
        var heartbeat = Heartbeat(
            WorkerStore(_ =>
            {
                entry.UntrackLock(released);
                return Lease(1);
            })
        );

        await heartbeat.TickAsync(TestContext.Current.CancellationToken);

        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    // The runway of a two-millisecond beat, whose derived lease rounds to nothing: one millisecond, so a fact about a
    // handler outrunning it waits a timer tick and no longer.
    private static readonly IOptions<JobsOptions> TinyRunway = Options(TimeSpan.FromMilliseconds(2), leaseTtlSeconds: 0);

    // Past the one-millisecond runway by any timer's reckoning.
    private static readonly TimeSpan TinyWait = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task A_handler_still_running_a_runway_after_a_beat_saw_its_timeout_is_named_once_and_stays_renewed()
    {
        using var entry = Held(Entry(1, leaseRunway: ClaimRunway, state: InFlightJobState.Executing));
        var held = new LockToken("orders.held", Guid.NewGuid());
        entry.TrackLock(held, ClaimRunway);
        await TimeOutAsync(entry);
        var log = new RecordingLogger();
        var heartbeat = Heartbeat(WorkerStore(Capture), TinyRunway, log: log);

        // The beat that first sees the timeout says nothing: the handler has the runway from here to unwind.
        Assert.True(await heartbeat.TickAsync(TestContext.Current.CancellationToken));
        Assert.Empty(log.Errors);

        // The handler runs on, so a beat after the runway names it, once, and its lease and lock are still asked for.
        await Task.Delay(TinyWait, TestContext.Current.CancellationToken);
        Assert.True(await heartbeat.TickAsync(TestContext.Current.CancellationToken));
        Assert.True(await heartbeat.TickAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ignores cancellation", Assert.Single(log.Errors));
        Assert.Equal("[{\"job_id\":1,\"execution_number\":0}]", _asked!.Parameters["p_executions"]);
        Assert.Contains(held.HoldToken.ToString("D"), Assert.IsType<string>(_asked.Parameters["p_locks"]));
    }

    [Fact]
    public async Task A_completion_still_being_written_and_a_single_shot_entry_are_not_named_past_the_timeout()
    {
        // Job 1's handler returned and its completion write is repeating; job 2 is a debug run with no lease source.
        using var finalizing = Held(Entry(1, leaseRunway: ClaimRunway, state: InFlightJobState.Finalizing));
        using var singleShot = Held(Entry(2, state: InFlightJobState.Executing));
        await TimeOutAsync(finalizing);
        await TimeOutAsync(singleShot);
        var log = new RecordingLogger();
        var heartbeat = Heartbeat(WorkerStore(Capture), TinyRunway, log: log);

        Assert.True(await heartbeat.TickAsync(TestContext.Current.CancellationToken));
        await Task.Delay(TinyWait, TestContext.Current.CancellationToken);
        Assert.True(await heartbeat.TickAsync(TestContext.Current.CancellationToken));

        Assert.Empty(log.Errors);
    }

    /// <summary>Fires the entry's execution timeout and returns once its attempt token is cancelled.</summary>
    private static async Task TimeOutAsync(InFlightJob entry)
    {
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = entry.AttemptToken.Register(fired.SetResult);
        entry.ArmTimeout(TimeSpan.Zero);
        await fired.Task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
    }

    /// <summary>An entry for job <paramref name="jobId"/> at <paramref name="executionNumber"/>, unregistered.</summary>
    internal static InFlightJob Entry(
        long jobId,
        long executionNumber = 0,
        TimeSpan? leaseRunway = null,
        InFlightJobState state = InFlightJobState.Claimed
    ) =>
        new(
            new ClaimedJob(
                JobId: jobId,
                JobRef: Guid.CreateVersion7(),
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: executionNumber,
                CorrelationKey: null,
                ConcurrencyKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0
            ),
            leaseRunway,
            CancellationToken.None
        )
        {
            State = state,
        };

    private InFlightJob Held(InFlightJob entry)
    {
        _registry.TryAdd(entry);
        return entry;
    }

    private WorkerHeartbeat Heartbeat(
        WorkersOperations workers,
        IOptions<JobsOptions>? options = null,
        ActaMetrics? metrics = null,
        ILogger? log = null
    )
    {
        var context = new WorkerContext(null);
        context.WorkerId = 1;
        return new WorkerHeartbeat(
            workers,
            ScriptedSession.Unreachable.GetRequiredService<DefinitionsOperations>(),
            options ?? Options(Interval, LeaseTtlSeconds),
            Registration,
            context,
            _registry,
            recoveryPass: null!,
            log ?? NullLogger.Instance,
            metrics
        );
    }

    // The beat's one call, kept for a fact that reads what it asked for, and answered with no rows.
    private BoundCall? _asked;

    private object? Capture(BoundCall call)
    {
        _asked = call;
        return null;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> format)
        {
            if (level == LogLevel.Error)
            {
                Errors.Add(format(state, exception));
            }
        }
    }

    // The worker lifecycle over a scripted lease extend, the heartbeat's only database call.
    private static WorkersOperations WorkerStore(Func<BoundCall, object?> answer) =>
        new ScriptedSession(answer).Services().GetRequiredService<WorkersOperations>();

    private static ExtendWorkerLeasesRow Lease(long jobId, bool renewed = true) =>
        new(jobId, ExecutionNumber: 0, HoldToken: null, renewed, DefinitionsModifiedAtUtc: null);

    private static ExtendWorkerLeasesRow Lock(LockToken hold) =>
        new(JobId: null, ExecutionNumber: null, hold.HoldToken, Renewed: true, DefinitionsModifiedAtUtc: null);
}
