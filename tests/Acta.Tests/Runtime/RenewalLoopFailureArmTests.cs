using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The failure arms of the two loops that keep an attempt alive: the worker heartbeat, which renews its job lease and
/// held locks, and the lease watchdog. Their happy paths and their single ticks are pinned elsewhere
/// (<see cref="WorkerHeartbeatLeaseRunwayTests"/> for what one tick decides,
/// <see cref="LoopTickCancellationFilterTests"/> for the stray-cancellation filter); what is pinned here
/// is the loop around the tick. Each of these tasks is awaited by the runtime host, so a tick failure
/// that escapes <c>RunAsync</c> does not stop one renewal - it faults the host's <c>WhenAll</c> and takes
/// the worker down, at exactly the moment (a database blip) the heartbeat exists to survive.
/// </summary>
/// <remarks>
/// The heartbeat already absorbs a store fault inside its own tick while the token is live, so the way
/// a fault reaches the loop's arms is the shutdown window: the call cancels the loop's token and then
/// fails rather than cancelling cleanly, which is what a connection torn down under a running query
/// looks like. The store here does exactly that, deterministically, with no timing dependence. The
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
            ScriptedWorkerStore(cts, failOn: 3, new InvalidOperationException("synthetic connection torn down")),
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
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
            ScriptedWorkerStore(cts, failOn: 3, cancellation: true),
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
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
            ScriptedWorkerStore(cts, failOn: 0, cancellation: true),
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
            LoopOptions(),
            Registration,
            context,
            log
        );

        await heartbeat.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(log.Errors);
    }

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

    private static WorkerContext HeartbeatContext()
    {
        var context = new WorkerContext(Registration);
        context.WorkerIdByNamespace["orders"] = 1;
        return context;
    }

    // The lease extend renews no rows on every call but the scripted one.
    private static WorkerLifecycle ScriptedWorkerStore(
        CancellationTokenSource cts,
        int failOn,
        Exception? failure = null,
        bool cancellation = false
    ) => FailingOn(cts, failOn, failure, cancellation).Services().GetRequiredService<WorkerLifecycle>();

    // Answers the Nth call by cancelling the loop's own token and then failing, which is a connection
    // torn down under a running query at shutdown: the exact state in which a tick failure reaches
    // RunAsync's arms rather than being absorbed inside TickAsync. Every other call renews normally.
    private static ScriptedSession FailingOn(CancellationTokenSource cts, int failOn, Exception? failure, bool cancellation)
    {
        var calls = 0;
        return new ScriptedSession(_ =>
        {
            if (Interlocked.Increment(ref calls) != failOn)
            {
                return null;
            }

            cts.Cancel();
            throw cancellation ? new OperationCanceledException(cts.Token) : failure!;
        });
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
