using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// What the claimer does when the claim query itself fails - a database outage, a failover, a connection pool
/// exhausted - in both profiles, which share the one loop. A failed claim must not tear the loop down and must not
/// retry at the anti-spin poll floor either - the floor exists for deadline races, and hammering a down database at
/// that cadence spams errors many times a second - so it backs off a full safety interval and says how long it is
/// waiting. A stop landing during that backoff, or on the claim call itself, must end the loop cleanly rather than
/// fault it, because the loop's task is awaited by the host's shutdown, and every permit the claim took comes back.
/// </summary>
public sealed class WorkerClaimerTests
{
    private const string Namespace = "orders";

    [Theory]
    [InlineData(ExecutionProfile.Buffered)]
    [InlineData(ExecutionProfile.Direct)]
    public async Task A_failing_claim_backs_off_and_keeps_claiming(ExecutionProfile profile)
    {
        var log = new RecordingLogger();
        var store = new ClaimStore(_ => throw new InvalidOperationException("synthetic database outage"));
        var (claimer, registry) = Claimer(store, log, profile, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var running = claimer.RunAsync(cts.Token, cts.Token);

        // Two failures is the fact: the loop retried after the first rather than exiting. A hang guard,
        // not a measurement - only a loop that stopped fails to reach this.
        await WaitUntil(() => ClaimErrors(log).Count >= 2, TimeSpan.FromSeconds(30));

        cts.Cancel();
        await running; // Completes rather than faults: the outage never escapes to the host's WhenAll.

        Assert.All(ClaimErrors(log), e => Assert.Contains("backing off 20ms before retry", e.Message, StringComparison.Ordinal));
        Assert.True(store.Calls >= 2);
        Assert.Equal(Capacity(profile), registry.Permits.CurrentCount);
    }

    [Fact]
    public async Task A_stop_during_the_backoff_ends_the_loop_instead_of_waiting_it_out()
    {
        // The backoff is a full safety interval - a minute in some deployments - so a stop landing inside
        // it has to break out. The safety interval here is 10 minutes: only the break makes this return.
        var log = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        var store = new ClaimStore(_ =>
        {
            cts.Cancel();
            throw new InvalidOperationException("synthetic connection torn down at shutdown");
        });
        var (claimer, _) = Claimer(store, log, ExecutionProfile.Buffered, TimeSpan.FromMinutes(10));

        await claimer.RunAsync(cts.Token, cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Single(ClaimErrors(log));
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task A_stop_landing_on_the_claim_call_ends_the_loop_without_calling_it_a_failure()
    {
        // The claim was cancelled by the stop, not broken by the database. Logging that as an error would put
        // a red line in every clean shutdown, and the permits still have to come back.
        var log = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        var store = new ClaimStore(ct =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var (claimer, registry) = Claimer(store, log, ExecutionProfile.Direct, TimeSpan.FromMinutes(10));

        await claimer.RunAsync(cts.Token, cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(1, store.Calls);
        Assert.Equal(Capacity(ExecutionProfile.Direct), registry.Permits.CurrentCount);
    }

    private static IReadOnlyList<RecordingLogger.Entry> ClaimErrors(RecordingLogger log) =>
        [.. log.Entries.Where(e => e.Level == LogLevel.Error && e.Message.Contains("claim iteration failed", StringComparison.Ordinal))];

    private static int Capacity(ExecutionProfile profile) => profile == ExecutionProfile.Direct ? 2 : 4;

    // The executor is null: these arms claim no row, so a null is the strongest statement that nothing runs.
    private static (WorkerClaimer Claimer, InFlightJobRegistry Registry) Claimer(
        ClaimStore store,
        RecordingLogger log,
        ExecutionProfile profile,
        TimeSpan safetyPoll
    )
    {
        var context = new WorkerContext(new WorkerRegistration(Namespace, null, null, [], []));
        context.NamespaceIds[Namespace] = 1;
        context.WorkerIdByNamespace[Namespace] = 1;
        var options = new JobsOptions
        {
            ExecutionProfile = profile,
            SafetyPollInterval = safetyPoll,
            MaxConcurrentExecutors = 2,
            ClaimBatchSize = 2,
        };
        var registry = new InFlightJobRegistry(options);
        return (new WorkerClaimer(store.Execution(), executor: null!, options, context, registry, new IdleWakeup(), log), registry);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the claim loop to log its backoff and retry.");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>The real execution group over a session whose claim answers the rows the script returns.</summary>
    internal sealed class ClaimStore(Func<CancellationToken, IReadOnlyList<ClaimJobsRow>> onClaim)
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IExecutionOperations Execution() => new ScriptedSession(
                (call, ct) =>
                {
                    if (call.Stem != "ClaimJobs")
                    {
                        throw new NotSupportedException(call.Stem);
                    }

                    Interlocked.Increment(ref _calls);
                    return Task.FromResult<object?>(onClaim(ct));
                }
            ).Services().GetRequiredService<IExecutionOperations>();
    }

    internal sealed class IdleWakeup : IWorkerWakeup
    {
        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            ValueTask.FromResult(WorkerWakeupWaitStatus.TimedOut);
    }

    internal sealed class RecordingLogger : ILogger
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
