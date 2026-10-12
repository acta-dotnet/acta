using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// What one heartbeat tick does to an entry's lease deadline. The <see cref="WorkerHeartbeat"/> renews the held
/// executions and handler-held locks in one call, re-arms each entry's deadline on a confirmed renewal, and cancels
/// outright on a definitive loss; a call that throws moves nothing, so an outage cancels by the deadline alone. The
/// deadlines are real timers, so these facts arm them short and wait on the attempt token (no DB).
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

    private static readonly WorkerRegistration Registration = new("orders", null, null, [], []);

    private readonly InFlightJobRegistry _registry = new(new JobsOptions());

    [Fact]
    public async Task A_confirmed_renewal_re_arms_the_lease_deadline()
    {
        using var entry = Held(Entry(1, leaseRunway: ShortLease));

        await Heartbeat(WorkerStore(_ => Lease(1))).TickAsync(TestContext.Current.CancellationToken);
        await Task.Delay(ShortLease * 2, TestContext.Current.CancellationToken);

        // The renewal moved the deadline to the runway past its request start, minutes away.
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_lease_lapse_cancels_the_attempt_the_margin_before_the_lease_expires()
    {
        // A one-second lease on a 10ms beat: the margin is (1000 - 10) / 2 = 495ms, so the renewed deadline falls 505ms
        // past the renewal's request start, and the attempt is cancelled well before the lease itself lapses at one
        // second. It also outlives its first deadline, which the renewal replaced.
        var options = Options(TimeSpan.FromMilliseconds(10), leaseTtlSeconds: 1);
        using var entry = Held(Entry(1, leaseRunway: ShortLease));
        var renewedAt = Stopwatch.GetTimestamp();

        await Heartbeat(WorkerStore(_ => Lease(1)), options).TickAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, entry.AttemptToken));

        Assert.InRange(Stopwatch.GetElapsedTime(renewedAt), InFlightJob.LeaseRunway(options.Value) - TimerSlack, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task An_authoritative_refresh_that_drops_the_execution_cancels_all_but_a_finalizing_entry()
    {
        using var claimed = Held(Entry(1));
        using var executing = Held(Entry(2, state: InFlightJobState.Executing));
        using var finalizing = Held(Entry(3, state: InFlightJobState.Finalizing));

        await Heartbeat(WorkerStore(_ => null)).TickAsync(TestContext.Current.CancellationToken);

        // The rows are absent from an authoritative answer (operator cancel / reclaimed lease): a claim or a running
        // handler stops now, while a finished attempt's completion write is left to its compare-and-swap.
        Assert.True(claimed.AttemptToken.IsCancellationRequested);
        Assert.True(executing.AttemptToken.IsCancellationRequested);
        Assert.False(finalizing.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_finalizing_entry_is_held_and_renewed()
    {
        using var entry = Held(Entry(1, leaseRunway: ShortLease, state: InFlightJobState.Finalizing));
        var session = new ScriptedSession(_ => Lease(1));

        await Heartbeat(session.Services().GetRequiredService<WorkersOperations>()).TickAsync(TestContext.Current.CancellationToken);
        await Task.Delay(ShortLease * 2, TestContext.Current.CancellationToken);

        Assert.Equal("""[{"job_id":1,"execution_number":0}]""", Assert.Single(session.Calls).Parameters["p_executions"]);
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_skipped_row_keeps_running_on_its_unmoved_deadline()
    {
        using var entry = Held(Entry(1, leaseRunway: ShortLease));
        var armedAt = Stopwatch.GetTimestamp();

        await Heartbeat(WorkerStore(_ => Lease(1, renewed: false))).TickAsync(TestContext.Current.CancellationToken);

        // Another transaction held the row: it is still this worker's, so the tick leaves it running, but its lease
        // did not move, so neither does its deadline.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, entry.AttemptToken));
        Assert.True(Stopwatch.GetElapsedTime(armedAt) >= ShortLease - TimerSlack);
    }

    [Fact]
    public async Task A_refresh_that_throws_moves_nothing_so_an_outage_cancels_by_the_deadline_alone()
    {
        using var entry = Held(Entry(1, leaseRunway: ShortLease));
        entry.TrackLock(new LockToken("orders.flaky", Guid.NewGuid()));
        var armedAt = Stopwatch.GetTimestamp();

        await Heartbeat(WorkerStore(_ => throw new InvalidOperationException("synthetic transient worker-store fault")))
            .TickAsync(TestContext.Current.CancellationToken);

        // Neither the job nor its unrenewed lock counts as lost on a call that threw.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(HangGuard, entry.AttemptToken));
        Assert.True(Stopwatch.GetElapsedTime(armedAt) >= ShortLease - TimerSlack);
    }

    [Fact]
    public async Task Worker_heartbeat_renews_a_held_lock_in_the_same_call()
    {
        var held = new LockToken("orders.a", Guid.NewGuid());
        var session = new ScriptedSession(_ => new[] { Lease(1), Lock(held.Key) });
        using var entry = Held(Entry(1));
        entry.TrackLock(held);

        await Heartbeat(session.Services().GetRequiredService<WorkersOperations>()).TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            $$"""[{"lock_key":"orders.a","hold_token":"{{held.HoldToken}}"}]""",
            Assert.Single(session.Calls).Parameters["p_locks"]
        );
        Assert.False(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_cancels_when_a_held_lock_is_not_renewed()
    {
        using var entry = Held(Entry(1));
        entry.TrackLock(new LockToken("orders.lost", Guid.NewGuid()));

        await Heartbeat(WorkerStore(_ => Lease(1))).TickAsync(TestContext.Current.CancellationToken);

        // The hold's token moved: another holder took the key, so the critical section is no longer exclusive.
        Assert.True(entry.AttemptToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Worker_heartbeat_ignores_a_lock_released_while_the_renewal_was_in_flight()
    {
        var released = new LockToken("orders.released", Guid.NewGuid());
        using var entry = Held(Entry(1));
        entry.TrackLock(released);

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

    /// <summary>An entry for job <paramref name="jobId"/> at <paramref name="executionNumber"/>, unregistered.</summary>
    internal static InFlightJob Entry(
        long jobId,
        int executionNumber = 0,
        TimeSpan? leaseRunway = null,
        InFlightJobState state = InFlightJobState.Claimed
    ) =>
        new(
            new ClaimedJob(
                JobId: jobId,
                JobRef: Guid.CreateVersion7(),
                NamespaceId: 1,
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: executionNumber,
                DeduplicationKey: null,
                CorrelationKey: null,
                ConcurrencyKey: null,
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
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

    private WorkerHeartbeat Heartbeat(WorkersOperations workers, IOptions<JobsOptions>? options = null)
    {
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        return new WorkerHeartbeat(
            workers,
            ScriptedSession.Unreachable.GetRequiredService<DefinitionsOperations>(),
            options ?? Options(Interval, LeaseTtlSeconds),
            Registration,
            context,
            _registry,
            NullLogger.Instance
        );
    }

    // The worker lifecycle over a scripted lease extend, the heartbeat's only database call.
    private static WorkersOperations WorkerStore(Func<BoundCall, object?> answer) =>
        new ScriptedSession(answer).Services().GetRequiredService<WorkersOperations>();

    private static ExtendWorkerLeasesRow Lease(long jobId, bool renewed = true) =>
        new(jobId, ExecutionNumber: 0, LockKey: null, renewed, DefinitionsModifiedAtUtc: null);

    private static ExtendWorkerLeasesRow Lock(string lockKey) =>
        new(JobId: null, ExecutionNumber: null, lockKey, Renewed: true, DefinitionsModifiedAtUtc: null);
}
