using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// What a worker does when the claim query itself fails - a database outage, a failover, a connection
/// pool exhausted - in both loop shapes the runtime ships: the producer/consumer dispatch loop
/// (<see cref="ExecutionProfile.Buffered"/>) and the combined claim-execute coordinator
/// (<see cref="ExecutionProfile.Direct"/> and <see cref="ExecutionProfile.Bulk"/>). Two arms per loop.
/// A failed claim must not tear the loop down and must not retry at the anti-spin poll floor either -
/// the floor exists for deadline races, and hammering a down database at that cadence spams errors many
/// times a second - so it backs off a full safety interval and says how long it is waiting. A stop or
/// drain landing during that backoff, or on the claim call itself, must end the loop cleanly rather
/// than fault it, because the loop's task is awaited by the host's shutdown.
/// </summary>
public sealed class WorkerLoopClaimFailureTests
{
    private const string Namespace = "orders";

    [Fact]
    public async Task A_failing_claim_in_the_dispatch_loop_backs_off_and_keeps_claiming()
    {
        var log = new RecordingLogger();
        var store = new ClaimStore(_ => throw new InvalidOperationException("synthetic database outage"));
        var loop = Loop(store, log, ExecutionProfile.Buffered, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var running = loop.RunLoopAsync(cts.Token);

        // Two failures is the fact: the loop retried after the first rather than exiting. A hang guard,
        // not a measurement - only a loop that stopped fails to reach this.
        await WaitUntil(() => ClaimErrors(log).Count >= 2, TimeSpan.FromSeconds(30));

        cts.Cancel();
        await running; // Completes rather than faults: the outage never escapes to the host's WhenAll.

        Assert.All(ClaimErrors(log), e => Assert.Contains("backing off 20ms before retry", e.Message, StringComparison.Ordinal));
        Assert.True(store.Calls >= 2);
    }

    [Fact]
    public async Task A_stop_during_the_dispatch_loop_backoff_ends_the_loop_instead_of_waiting_it_out()
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
        var loop = Loop(store, log, ExecutionProfile.Buffered, TimeSpan.FromMinutes(10));

        await loop.RunLoopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Single(ClaimErrors(log));
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task A_failing_claim_in_the_combined_loop_backs_off_and_keeps_claiming()
    {
        var log = new RecordingLogger();
        var store = new ClaimStore(_ => throw new InvalidOperationException("synthetic database outage"));
        var loop = Loop(store, log, ExecutionProfile.Direct, TimeSpan.FromMilliseconds(20));

        using var cts = new CancellationTokenSource();
        var running = loop.RunLoopAsync(cts.Token);

        await WaitUntil(() => CombinedClaimErrors(log).Count >= 2, TimeSpan.FromSeconds(30));

        cts.Cancel();
        // The coordinator's drain re-acquires every executor permit before returning. A claim that threw
        // must have released the permits it took, or this never completes.
        await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.All(CombinedClaimErrors(log), e => Assert.Contains("backing off 20ms before retry", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stop_landing_on_the_combined_claim_call_ends_the_loop_without_calling_it_a_failure()
    {
        // The claim was cancelled by the drain, not broken by the database. Logging that as an error
        // would put a red line in every clean shutdown, and the permits still have to come back or the
        // coordinator's drain hangs.
        var log = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        var store = new ClaimStore(ct =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        var loop = Loop(store, log, ExecutionProfile.Direct, TimeSpan.FromMinutes(10));

        await loop.RunLoopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(1, store.Calls);
    }

    // ---------- helpers ----------

    private static IReadOnlyList<RecordingLogger.Entry> ClaimErrors(RecordingLogger log) =>
        [.. log.Entries.Where(e => e.Level == LogLevel.Error && e.Message.Contains("claim iteration failed", StringComparison.Ordinal))];

    private static IReadOnlyList<RecordingLogger.Entry> CombinedClaimErrors(RecordingLogger log) =>
        [
            .. log.Entries.Where(e =>
                e.Level == LogLevel.Error && e.Message.Contains("combined claim iteration failed", StringComparison.Ordinal)
            ),
        ];

    private static WorkerLoop Loop(ClaimStore store, RecordingLogger log, ExecutionProfile profile, TimeSpan safetyPoll)
    {
        var registration = new WorkerRegistration(Namespace, null, null, [], []);
        var context = new WorkerContext(registration);
        context.NamespaceIds[Namespace] = 1;
        context.WorkerIdByNamespace[Namespace] = 1;
        var options = Options.Create(
            new JobsOptions
            {
                ExecutionProfile = profile,
                SafetyPollInterval = safetyPoll,
                MaxConcurrentExecutors = 2,
                ClaimBatchSize = 2,
            }
        );

        // The executor is never reached: no claim ever returns a row, so a null here is the strongest
        // available statement that these arms run before any job is dispatched.
        return new WorkerLoop(store, executor: null!, options, registration, context, new IdleWakeup(), log);
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

    // ---------- test doubles ----------

    private sealed class ClaimStore(Func<CancellationToken, ClaimResult> onClaim) : IExecutionStore
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(onClaim(ct));
        }

        public Task<ClaimResult> ClaimOneAsync(ClaimRequest request, int leaseTtlSeconds, long? jobId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StartExecutionAction> StartExecutionAsync(
            long jobId,
            int workerId,
            int expectedExecutionNumber,
            int expectedVersion,
            int leaseTtlSeconds,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(
            IReadOnlyList<CompleteExecutionRequest> requests,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(int namespaceId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StartStepDecision> StartStepAsync(long jobId, string name, bool atMostOnce, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CompleteStepDecision> CompleteStepAsync(CompleteStepCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CheckpointSlotRow> CheckpointSlotAsync(CheckpointSlotCommand command, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RecordJobNoteAsync(long jobId, string message, JobPayload? detail, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<long>> GetChildJobIdsAsync(long parentJobId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Acta.Runtime.Modules.Execution.ChildLatches.StaleChildLatch>> GetStaleChildLatchesAsync(
            int namespaceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Acta.Runtime.Modules.Execution.Timers.SleepDecision> ArmOrConsumeSleepTimerAsync(
            ArmOrConsumeSleepTimerCommand command,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class IdleWakeup : IWorkerWakeup
    {
        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            ValueTask.FromResult(WorkerWakeupWaitStatus.TimedOut);
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
}
