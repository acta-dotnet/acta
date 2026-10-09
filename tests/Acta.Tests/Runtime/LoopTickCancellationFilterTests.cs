using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Covers findings 3/4 of the 2026-07-09 exception-handling review: <see cref="WorkerHeartbeat.RunAsync"/>
/// must catch a stray (non-cancellation)
/// <see cref="OperationCanceledException"/> raised mid-tick the same way the claim loop does - log and
/// keep looping - rather than let it escape to <c>Task.WhenAll</c> and kill the host. Before the fix, the
/// inner catch filtered on exception type (<c>ex is not OperationCanceledException</c>), so a stray OCE
/// (token not cancelled) matched neither the inner nor the outer catch and escaped <c>RunAsync</c>. The
/// fix filters on token state (<c>!ct.IsCancellationRequested</c>) instead.
/// </summary>
public sealed class LoopTickCancellationFilterTests
{
    private static readonly CancellationToken NeverCancelled = CancellationToken.None;

    [Fact]
    public async Task WorkerHeartbeat_tolerates_a_stray_OCE_from_the_lease_extend_and_keeps_running()
    {
        // A transient store fault surfaces the lease extend as a stray (non-cancellation) OCE. During
        // normal running (ct not cancelled) the heartbeat now catches it INSIDE the tick, warns, and lets
        // the per-attempt lease runway decide - so it warns rather than errors, and must never escape
        // RunAsync (which would kill the host).
        var failure = new OperationCanceledException("synthetic provider timeout", NeverCancelled);
        var logger = new RecordingLogger();
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) });

        var heartbeat = new WorkerHeartbeat(
            Workers(_ => throw failure),
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
            options,
            registration,
            context,
            logger
        );

        using var cts = new CancellationTokenSource();
        var runTask = heartbeat.RunAsync(cts.Token);

        // The immediate first tick and at least one PeriodicTimer tick each catch the throw and warn.
        await WaitUntil(
            () => logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("could not extend worker leases")) >= 2,
            TimeSpan.FromSeconds(5)
        );

        cts.Cancel();
        await runTask; // Must complete cleanly, not fault - proves the stray OCE never escaped RunAsync.

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task WorkerHeartbeat_logs_a_stray_OCE_from_the_catalog_reload_and_keeps_running()
    {
        // The lease extend answers a definition stamp; the catalog read behind it fails on every tick, since a
        // failed reload leaves the stamp unseen and is retried on the next one.
        var failure = new OperationCanceledException("synthetic provider timeout", NeverCancelled);
        var logger = new RecordingLogger();
        var context = new WorkerContext(null);
        context.NamespaceIds["orders"] = 1;
        context.WorkerIdByNamespace["orders"] = 1;
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) });
        var services = new ScriptedSession(call =>
            call.Stem == "GetDefinitionContracts"
                ? throw failure
                : new ExtendWorkerLeasesRow(JobId: null, ExecutionNumber: null, LockKey: null, Renewed: false, DateTime.UtcNow)
        ).Services();

        var heartbeat = new WorkerHeartbeat(
            services.GetRequiredService<WorkerLifecycle>(),
            services.GetRequiredService<GetDefinitionContractsHandler>(),
            options,
            registration,
            context,
            logger
        );

        using var cts = new CancellationTokenSource();
        var runTask = heartbeat.RunAsync(cts.Token);

        // A hang guard, not a measurement: the fact is that the loop keeps ticking after a stray OCE,
        // and the two entries it needs arrive on a 20ms cadence. Only a loop that stopped reaches this.
        await WaitUntil(() => logger.Entries.Count(e => e.Level == LogLevel.Error) >= 2, TimeSpan.FromSeconds(30));

        cts.Cancel();
        await runTask; // Must complete cleanly, not fault - proves the stray OCE never escaped RunAsync.

        Assert.All(logger.Entries.Where(e => e.Level == LogLevel.Error), e => Assert.Contains("heartbeat tick failed", e.Message));
    }

    [Fact]
    public async Task WorkerHeartbeat_completes_cleanly_when_a_non_OCE_tick_failure_lands_after_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var db = CancelThenThrowWorkers(cts, new InvalidOperationException("synthetic connection torn down"));
        var logger = new RecordingLogger();
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) });

        var heartbeat = new WorkerHeartbeat(
            db,
            ScriptedSession.Unreachable.GetRequiredService<GetDefinitionContractsHandler>(),
            options,
            registration,
            context,
            logger
        );

        // The immediate first tick hits the DB call, which cancels ct and then throws a non-cancellation
        // exception - the shutdown-window shape the fix must not let escape RunAsync (and Task.WhenAll).
        await heartbeat.RunAsync(cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("heartbeat tick failed"));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the expected tick failures to be logged.");
            }
            await Task.Delay(10);
        }
    }

    // The worker lifecycle over a scripted session: the heartbeat's only database call is the lease extend.
    private static WorkerLifecycle Workers(Func<BoundCall, object?> answer) =>
        new ScriptedSession(answer).Services().GetRequiredService<WorkerLifecycle>();

    // Cancels the loop's own token from inside the lease extend, then throws a non-cancellation
    // exception - reproduces "ct already cancelled" at the exact moment TickAsync's exception
    // surfaces to RunAsync's catches.
    private static WorkerLifecycle CancelThenThrowWorkers(CancellationTokenSource cts, Exception toThrow) =>
        Workers(_ =>
        {
            cts.Cancel();
            throw toThrow;
        });

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
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
