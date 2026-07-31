using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Covers findings 3/4 of the 2026-07-09 exception-handling review: <see cref="WorkerHeartbeat.RunAsync"/>
/// and <see cref="DefinitionPolicyReloader.RunAsync"/> must catch a stray (non-cancellation)
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

        var heartbeat = new WorkerHeartbeat(new ThrowingWorkerStore(failure), options, registration, context, logger);

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
    public async Task DefinitionPolicyReloader_logs_a_stray_OCE_tick_failure_and_keeps_running()
    {
        var failure = new OperationCanceledException("synthetic provider timeout", NeverCancelled);
        var logger = new RecordingLogger();
        var context = new WorkerContext(null);
        context.NamespaceIds["orders"] = 1;
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { SafetyPollInterval = TimeSpan.FromMilliseconds(20) });

        var reloader = new DefinitionPolicyReloader(new ThrowingDefinitionStore(failure), options, registration, context, logger);

        using var cts = new CancellationTokenSource();
        var runTask = reloader.RunAsync(cts.Token);

        await WaitUntil(() => logger.Entries.Count(e => e.Level == LogLevel.Error) >= 2, TimeSpan.FromSeconds(5));

        cts.Cancel();
        await runTask; // Must complete cleanly, not fault - proves the stray OCE never escaped RunAsync.

        Assert.All(
            logger.Entries.Where(e => e.Level == LogLevel.Error),
            e => Assert.Contains("definition-policy reload tick failed", e.Message)
        );
    }

    [Fact]
    public async Task WorkerHeartbeat_completes_cleanly_when_a_non_OCE_tick_failure_lands_after_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var db = new CancelThenThrowWorkerStore(cts, new InvalidOperationException("synthetic connection torn down"));
        var logger = new RecordingLogger();
        var context = new WorkerContext(null);
        context.WorkerIdByNamespace["orders"] = 1;
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) });

        var heartbeat = new WorkerHeartbeat(db, options, registration, context, logger);

        // The immediate first tick hits the DB call, which cancels ct and then throws a non-cancellation
        // exception - the shutdown-window shape the fix must not let escape RunAsync (and Task.WhenAll).
        await heartbeat.RunAsync(cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("heartbeat tick failed"));
    }

    [Fact]
    public async Task DefinitionPolicyReloader_completes_cleanly_when_a_non_OCE_tick_failure_lands_after_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var context = new WorkerContext(null);
        context.NamespaceIds["orders"] = 1;
        var registration = new WorkerRegistration("orders", null, null, [], []);
        var options = Options.Create(new JobsOptions { SafetyPollInterval = TimeSpan.FromMilliseconds(20) });

        var reloader = new DefinitionPolicyReloader(
            new CancelThenThrowDefinitionStore(cts, new InvalidOperationException("synthetic connection torn down")),
            options,
            registration,
            context,
            logger
        );

        // The first periodic tick hits the DB call, which cancels ct and then throws a non-cancellation
        // exception - the shutdown-window shape the fix must not let escape RunAsync (and Task.WhenAll).
        await reloader.RunAsync(cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("definition-policy reload tick failed"));
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

    // Worker-store seams for WorkerHeartbeat: the heartbeat's only store call is the lease extend.
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

        public ValueTask<JobWorkerDetail?> GetWorkerAsync(int workerId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ThrowingWorkerStore(Exception failure) : WorkerStoreStub
    {
        public override Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(
            int workerId,
            int leaseTtlSeconds,
            bool draining,
            CancellationToken ct
        ) => throw failure;
    }

    // Cancels the loop's own token from inside the store call, then throws a non-cancellation
    // exception - reproduces "ct already cancelled" at the exact moment TickAsync's exception
    // surfaces to RunAsync's catches.
    private sealed class CancelThenThrowWorkerStore(CancellationTokenSource cts, Exception toThrow) : WorkerStoreStub
    {
        public override Task<IReadOnlyList<long>> ExtendWorkerLeasesAsync(
            int workerId,
            int leaseTtlSeconds,
            bool draining,
            CancellationToken ct
        )
        {
            cts.Cancel();
            throw toThrow;
        }
    }

    // Cancels the loop's own token from inside the store call, then throws a non-cancellation
    // exception - the shutdown-window shape RunAsync's catch ordering must swallow.
    private sealed class CancelThenThrowDefinitionStore(CancellationTokenSource cts, Exception toThrow) : IDefinitionStore
    {
        public Task<IReadOnlyList<StoredDefinitionContract>> GetDefinitionContractsAsync(short namespaceId, CancellationToken ct)
        {
            cts.Cancel();
            throw toThrow;
        }

        public ValueTask<Acta.JobDefinitionDetail?> GetDefinitionAsync(int definitionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DefinitionPage> ListDefinitionsAsync(DefinitionPageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DefinitionOverrideOutcome> SetDefinitionOverridesAsync(SetDefinitionOverridesCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }

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

    private sealed class ThrowingDefinitionStore(Exception failure) : IDefinitionStore
    {
        public Task<IReadOnlyList<StoredDefinitionContract>> GetDefinitionContractsAsync(short namespaceId, CancellationToken ct) =>
            throw failure;

        public ValueTask<Acta.JobDefinitionDetail?> GetDefinitionAsync(int definitionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DefinitionPage> ListDefinitionsAsync(DefinitionPageRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, int>> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DefinitionOverrideOutcome> SetDefinitionOverridesAsync(SetDefinitionOverridesCommand command, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
