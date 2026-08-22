using System.Diagnostics;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Locks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The failure arms of the three loops that keep an attempt alive: the lock-lease renewer, the worker
/// heartbeat, and the lease watchdog. Their happy paths and their single ticks are pinned elsewhere
/// (<see cref="WorkerHeartbeatLeaseRunwayTests"/> for what one tick decides,
/// <see cref="LoopTickCancellationFilterTests"/> for the stray-cancellation filter); what is pinned here
/// is the loop around the tick. Each of these tasks is awaited by the runtime host, so a tick failure
/// that escapes <c>RunAsync</c> does not stop one renewal - it faults the host's <c>WhenAll</c> and takes
/// the worker down, at exactly the moment (a database blip) the renewers exist to survive.
/// </summary>
/// <remarks>
/// Both renewers already absorb a store fault inside their own tick while the token is live, so the way
/// a fault reaches the loop's arms is the shutdown window: the call cancels the loop's token and then
/// fails rather than cancelling cleanly, which is what a connection torn down under a running query
/// looks like. The stores here do exactly that, deterministically, with no timing dependence. The
/// watchdog does no I/O at all, so its tick failure is injected at its clock seam and it is the one loop
/// whose carry-on is directly observable.
/// </remarks>
public sealed class RenewalLoopFailureArmTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);
    private const int LeaseTtlSeconds = 80;

    private static IOptions<JobsOptions> LoopOptions() =>
        Options.Create(new JobsOptions { HeartbeatInterval = Interval, LeaseTtlSeconds = LeaseTtlSeconds });

    private static readonly WorkerRegistration Registration = new("orders", null, null, [], []);

    // ---- LockLeaseHeartbeat ----

    [Fact]
    public async Task A_lock_lease_tick_that_fails_before_the_first_beat_is_logged_and_the_loop_still_exits_cleanly()
    {
        // The lock store fails on the immediate startup tick. The loop must record it as an error - a
        // handler's mutual exclusion is now running on an unrenewed lease - and then leave through its
        // normal shutdown path rather than propagating into the host.
        var (context, attempt, token) = Attempt();
        using var cts = new CancellationTokenSource();
        var log = new RecordingLogger();
        var heartbeat = new LockLeaseHeartbeat(
            new ScriptedLockStore(cts, failOn: 1, new InvalidOperationException("synthetic connection torn down")),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var error = Assert.Single(log.Errors);
        Assert.Contains("initial lock-lease tick failed", error.Message, StringComparison.Ordinal);

        // Nothing was fed and nothing was cancelled: an uncertain extend leaves the deadline for the
        // watchdog, which is the whole reason a failed renewal is survivable.
        Assert.False(attempt.Cts.IsCancellationRequested);
        Assert.True(attempt.Attempt.Holds(token));
    }

    [Fact]
    public async Task A_periodic_lock_lease_tick_that_fails_is_logged_as_retrying_next_tick()
    {
        // The startup tick and a full periodic beat renew normally; the failure lands on the beat after
        // that, which is the arm with its own message - "retrying next tick" - so an operator reading the
        // log can tell a worker that never renewed from one that renewed and then lost the store.
        var (context, attempt, _) = Attempt();
        using var cts = new CancellationTokenSource();
        var log = new RecordingLogger();
        var heartbeat = new LockLeaseHeartbeat(
            new ScriptedLockStore(cts, failOn: 3, new InvalidOperationException("synthetic connection torn down")),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var error = Assert.Single(log.Errors);
        Assert.Contains("lock-lease tick failed; retrying next tick", error.Message, StringComparison.Ordinal);
        Assert.False(attempt.Cts.IsCancellationRequested);
    }

    [Fact]
    public async Task A_periodic_lock_lease_tick_cancelled_by_shutdown_leaves_without_an_error()
    {
        // Cancellation is not a renewal failure. Logging it as one would put an error in every clean
        // stop, which is how a real one stops being noticed.
        var (context, _, _) = Attempt();
        using var cts = new CancellationTokenSource();
        var log = new RecordingLogger();
        var heartbeat = new LockLeaseHeartbeat(
            new ScriptedLockStore(cts, failOn: 3, cancellation: true),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(log.Errors);
    }

    [Fact]
    public async Task A_shutdown_that_lands_before_the_first_lock_lease_tick_is_not_an_error_either()
    {
        var (context, _, _) = Attempt();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var log = new RecordingLogger();
        var heartbeat = new LockLeaseHeartbeat(
            new ScriptedLockStore(cts, failOn: 0, cancellation: true),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(log.Errors);
    }

    // ---- WorkerHeartbeat ----

    [Fact]
    public async Task A_periodic_worker_heartbeat_tick_that_fails_is_logged_as_retrying_next_tick()
    {
        // The batched job-lease renewal is what keeps every row this worker owns out of reach of
        // recovery. Its periodic arm carries a different message from the startup arm for the same
        // reason as the lock loop, and it must not escape the loop.
        var context = HeartbeatContext();
        using var cts = new CancellationTokenSource();
        var log = new RecordingLogger();
        var heartbeat = new WorkerHeartbeat(
            new ScriptedWorkerStore(cts, failOn: 3, new InvalidOperationException("synthetic connection torn down")),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var error = Assert.Single(log.Errors);
        Assert.Contains("heartbeat tick failed; retrying next tick", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_periodic_worker_heartbeat_tick_cancelled_by_shutdown_leaves_without_an_error()
    {
        var context = HeartbeatContext();
        using var cts = new CancellationTokenSource();
        var log = new RecordingLogger();
        var heartbeat = new WorkerHeartbeat(
            new ScriptedWorkerStore(cts, failOn: 3, cancellation: true),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(log.Errors);
    }

    [Fact]
    public async Task A_shutdown_that_lands_before_the_first_worker_heartbeat_tick_is_not_an_error()
    {
        // The startup tick stamps last_seen immediately rather than waiting out the first interval, so it
        // is the tick most likely to collide with a stop during a fast restart loop. That collision is
        // routine and must leave no error behind.
        var context = HeartbeatContext();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var log = new RecordingLogger();
        var heartbeat = new WorkerHeartbeat(
            new ScriptedWorkerStore(cts, failOn: 0, cancellation: true),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(log.Errors);
    }

    // ---- AttemptWatchdog ----

    [Fact]
    public async Task A_watchdog_tick_that_throws_is_logged_and_the_next_tick_still_enforces_the_deadline()
    {
        // The watchdog is the fail-safe half of the bounded fail-open: it is what cancels an attempt
        // whose lease is about to lapse while the renewers are down. If one bad tick ended the loop, the
        // failure mode is the one the design exists to prevent - a handler still writing after another
        // worker has reclaimed its job. So the contract is not "logs" but "logs and keeps enforcing".
        var context = new WorkerContext(Registration);
        using var attemptCts = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = attemptCts.Token.Register(() => cancelled.TrySetResult());

        // A fresh attempt's job-lease deadline is 0, i.e. long lapsed on the monotonic clock, so any tick
        // that actually runs must cancel it.
        context.RunningAttempts[1] = new RunningAttempt(attemptCts);

        var log = new RecordingLogger();
        var firstTickThrew = 0;
        var watchdog = new AttemptWatchdog(
            LoopOptions(),
            Registration,
            context,
            log,
            () =>
                Interlocked.Exchange(ref firstTickThrew, 1) == 0
                    ? throw new InvalidOperationException("synthetic watchdog clock fault")
                    : Stopwatch.GetTimestamp()
        );

        using var cts = new CancellationTokenSource();
        var running = watchdog.RunAsync(cts.Token);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        cts.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Contains(log.Errors, e => e.Message.Contains("lease watchdog tick failed; retrying next tick", StringComparison.Ordinal));
        Assert.True(attemptCts.IsCancellationRequested);
    }

    // ---------- helpers ----------

    private static (WorkerContext Context, TrackedAttempt Attempt, LockToken Token) Attempt()
    {
        var context = new WorkerContext(Registration);
        context.WorkerIdByNamespace["orders"] = 1;
        var cts = new CancellationTokenSource();
        var attempt = new RunningAttempt(cts);
        var token = new LockToken("orders.charge", Guid.NewGuid());
        attempt.TrackLock(token, Stopwatch.GetTimestamp() + (long)(LeaseTtlSeconds * (double)Stopwatch.Frequency));
        context.RunningAttempts[1] = attempt;
        return (context, new TrackedAttempt(attempt, cts), token);
    }

    private static WorkerContext HeartbeatContext()
    {
        var context = new WorkerContext(Registration);
        context.WorkerIdByNamespace["orders"] = 1;
        return context;
    }

    private sealed record TrackedAttempt(RunningAttempt Attempt, CancellationTokenSource Cts);

    // ---------- test doubles ----------

    // Answers the Nth call by cancelling the loop's own token and then failing, which is a connection
    // torn down under a running query at shutdown: the exact state in which a tick failure reaches
    // RunAsync's arms rather than being absorbed inside TickAsync. Every other call renews normally.
    private sealed class ScriptedLockStore(CancellationTokenSource cts, int failOn, Exception? failure = null, bool cancellation = false)
        : ILockStore
    {
        private int _calls;

        public Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) != failOn)
            {
                return Task.FromResult(true);
            }

            cts.Cancel();
            throw cancellation ? new OperationCanceledException(cts.Token) : failure!;
        }

        public Task<bool> ReleaseAsync(LockToken token, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ScriptedWorkerStore(CancellationTokenSource cts, int failOn, Exception? failure = null, bool cancellation = false)
        : IWorkerStore
    {
        private int _calls;

        public Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(int workerId, int leaseTtlSeconds, bool draining, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) != failOn)
            {
                return Task.FromResult<IReadOnlyList<long>>([]);
            }

            cts.Cancel();
            throw cancellation ? new OperationCanceledException(cts.Token) : failure!;
        }

        public Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) => throw new NotSupportedException();

        public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkerPage> ListWorkersAsync(WorkerPageRequest request, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<WorkerDetail?> GetWorkerAsync(Guid workerRef, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Errors
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries.Where(e => e.Level == LogLevel.Error)];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_entries)
            {
                _entries.Add(new Entry(logLevel, formatter(state, exception)));
            }
        }

        public sealed record Entry(LogLevel Level, string Message);
    }
}
