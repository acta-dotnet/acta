using System.Collections.Concurrent;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Checkpoints;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The degraded half of the Bulk group-commit flush, driven at the sink's own seam over a scripted
/// <see cref="IExecutionStore"/>. Bulk's stated contract is that a flush is not all-or-nothing past the
/// set call: the set-based commit and each per-job fallback are separate transactions, so a mid-flush
/// failure leaves the already-committed rows terminal and only the rest for recovery, and the log names
/// the jobs that are actually unfinalized rather than the whole batch. Every fact here is that sentence
/// cut into pieces: the one path that may claim a whole-batch rollback, the per-job fallback for rows the
/// set call self-filtered, the <c>unresolved</c> bookkeeping, the fallback CAS that matched nothing, and
/// the two wakeups a released parent depends on.
/// </summary>
/// <remarks>
/// The equivalent facts against a real ledger live in <c>CompletionSinkBulkFallbackSpec</c>, which skips
/// on SQLite (no <c>complete_executions_batch</c> routine there, so Bulk degrades to Direct). These run
/// on every leg, including the one a contributor runs locally.
/// </remarks>
public sealed class CompletionSinkDegradedFlushTests
{
    [Fact]
    public async Task A_failed_set_call_leaves_every_job_for_recovery_and_touches_nothing_else()
    {
        // One statement, one commit: nothing landed. The batch must not then be picked apart per job
        // (that would re-complete rows the transaction may yet have written) and must not publish a
        // wakeup for a job that is still Executing. This is the only path allowed to claim a rollback.
        var store = new ScriptedExecutionStore { BatchFailure = new InvalidOperationException("deadlock victim") };
        var wakes = new WakeupSpy();
        var log = new RecordingLogger();
        var sink = Sink(store, wakes, log);

        await FlushAsync(sink, Buffered(11), Buffered(12), Buffered(13));

        Assert.Empty(store.FallbackRequests);
        Assert.Empty(wakes.Wakes);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Bulk completion flush of 3 jobs failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains("remain Executing for recovery", entry.Message, StringComparison.Ordinal);
        Assert.Same(store.BatchFailure, entry.Exception);
    }

    [Fact]
    public async Task A_row_the_set_call_self_filtered_is_completed_per_job_with_full_semantics()
    {
        // The set-based routine reports which ordinals it did NOT finalize (a parent, or a lost lease).
        // Those, and only those, go back through scalar CompleteExecution so the parent child-done latch
        // runs; the finalized ones must not be submitted twice.
        var store = new ScriptedExecutionStore { Finalized = [true, false], Fallback = _ => Completed(parentReleased: false) };
        var wakes = new WakeupSpy();
        var sink = Sink(store, wakes, new RecordingLogger());

        await FlushAsync(sink, Buffered(21), Buffered(22));

        var fallback = Assert.Single(store.FallbackRequests);
        Assert.Equal(22, fallback.JobId);
        Assert.Equal(new long[] { 21, 22 }, store.BatchRequests.Select(r => r.JobId).ToArray());
    }

    [Fact]
    public async Task A_failed_fallback_names_only_its_own_job_and_lets_the_rest_of_the_batch_finish()
    {
        // The contract the old single-catch flush broke: one failing fallback must not strand the rows
        // after it, must not be reported as a rollback, and the log must name the jobs that are actually
        // unfinalized. Job 32 fails; 31 was already committed by the set call and 33 completes after it.
        var store = new ScriptedExecutionStore
        {
            Finalized = [true, false, false],
            Fallback = request =>
                request.JobId == 32 ? throw new InvalidOperationException("connection reset") : Completed(parentReleased: false),
        };
        var wakes = new WakeupSpy();
        var log = new RecordingLogger();
        var sink = Sink(store, wakes, log);

        await FlushAsync(sink, Buffered(31), Buffered(32), Buffered(33));

        // Iteration did not stop at the failure: the row after it still went through the fallback.
        Assert.Equal(new long[] { 32, 33 }, store.FallbackRequests.Select(r => r.JobId).ToArray());

        // The committed row still got its deferred wake, and so did the one that completed after the fail.
        Assert.Equal(2, wakes.Wakes.Count(w => w.Channel.Kind == WorkerWakeupChannelKind.JobCompletion));
        Assert.Contains(wakes.Wakes, w => w.Channel == WorkerWakeupChannel.JobCompletion(31));
        Assert.Contains(wakes.Wakes, w => w.Channel == WorkerWakeupChannel.JobCompletion(33));
        Assert.DoesNotContain(wakes.Wakes, w => w.Channel == WorkerWakeupChannel.JobCompletion(32));

        // One error, naming one job out of three - not "the batch failed".
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("Bulk completion left 1 jobs unfinalized", entry.Message, StringComparison.Ordinal);
        Assert.Contains("of 3 in the batch: 32", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("31", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("33", entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public Task A_fallback_cas_answering_not_owner_is_said_out_loud_and_wakes_nobody() =>
        AssertFallbackCasMatchedNothing(CompleteExecutionAction.NotOwner);

    [Fact]
    public Task A_fallback_cas_answering_already_terminal_is_said_out_loud_and_wakes_nobody() =>
        AssertFallbackCasMatchedNothing(CompleteExecutionAction.AlreadyTerminal);

    private static async Task AssertFallbackCasMatchedNothing(CompleteExecutionAction action)
    {
        // An external control or a reclaim moved the row while the completion sat buffered. Nothing was
        // finalized here, so no wakeup is owed - but the buffered completion must not vanish without a
        // trace, because the row is now owned by recovery or by whoever won the race.
        var store = new ScriptedExecutionStore
        {
            Finalized = [false],
            Fallback = _ => new CompleteExecutionResult(action, (byte)JobStatusCode.Ready, null, DateTime.UtcNow, ParentReleased: false),
        };
        var wakes = new WakeupSpy();
        var log = new RecordingLogger();
        var sink = Sink(store, wakes, log);

        await FlushAsync(sink, Buffered(41));

        Assert.Empty(wakes.Wakes);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Bulk fallback completion for job 41", entry.Message, StringComparison.Ordinal);
        Assert.Contains(action.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Contains("nothing was finalized here", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fallback_that_releases_a_parent_publishes_the_finish_wake_and_the_parent_wake()
    {
        // The child-done latch flipped a Suspended parent to Ready. The parent can live in another
        // namespace, so the release is announced to all worker namespaces; the job-finished wake is what
        // a colocated RunAndWaitAsync caller is blocked on. A parent that never wakes is the failure
        // this pair prevents.
        var store = new ScriptedExecutionStore { Finalized = [false], Fallback = _ => Completed(parentReleased: true) };
        var wakes = new WakeupSpy();
        var sink = Sink(store, wakes, new RecordingLogger());

        await FlushAsync(sink, Buffered(51));

        Assert.Equal(2, wakes.Wakes.Count);
        Assert.Contains(wakes.Wakes, w => w.Channel == WorkerWakeupChannel.JobCompletion(51) && w.Reason == WorkerWakeupReason.JobFinished);
        Assert.Contains(
            wakes.Wakes,
            w => w.Channel.Kind == WorkerWakeupChannelKind.AllWorkerNamespaces && w.Reason == WorkerWakeupReason.WorkAvailable
        );
    }

    [Fact]
    public async Task A_fallback_that_lands_non_terminal_publishes_neither_wake()
    {
        // A buffered completion is always a plain terminal landing, so a non-terminal final status means
        // the routine re-armed the row instead. No caller is waiting on a finish that did not happen and
        // no parent was released, so the sink stays silent rather than waking on a Ready row.
        var store = new ScriptedExecutionStore
        {
            Finalized = [false],
            Fallback = _ => new CompleteExecutionResult(
                CompleteExecutionAction.Completed,
                (byte)JobStatusCode.Ready,
                null,
                DateTime.UtcNow,
                ParentReleased: false
            ),
        };
        var wakes = new WakeupSpy();
        var log = new RecordingLogger();
        var sink = Sink(store, wakes, log);

        await FlushAsync(sink, Buffered(61));

        Assert.Empty(wakes.Wakes);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_partial_batch_is_flushed_on_the_interval_window_not_only_on_size()
    {
        // Without the window a lone completion sits in the buffer until enough others arrive, which on a
        // quiet queue is unbounded. The batch cap is 100 here and exactly one job is buffered, so only
        // the interval trigger can produce a flush at all.
        var store = new ScriptedExecutionStore { Finalized = [true] };
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.OnBatch = _ => flushed.TrySetResult();
        var sink = new CompletionSink(
            store,
            new WorkerWakeupPublisher(new WakeupSpy()),
            Options.Create(
                new JobsOptions
                {
                    BatchCompletionSize = 100,
                    BatchCompletionInterval = TimeSpan.FromMilliseconds(20),
                    BatchCompletionMaxBytes = int.MaxValue,
                }
            ),
            new RecordingLogger()
        );

        var flusher = sink.RunFlusherAsync();
        await sink.EnqueueAsync(Buffered(71));

        // Completing the writer only after the window fired proves the flush was the window's doing.
        await flushed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        sink.CompleteWriter();
        await flusher;

        Assert.Equal(1, Assert.Single(store.BatchSizes));
    }

    [Fact]
    public async Task Parallel_flushers_drain_the_shared_buffer_with_each_completion_committed_once()
    {
        // Several flushers share one multi-reader buffer so group commit does not serialize every
        // completion through one connection. At-least-once is the crash contract, not a licence to
        // double-commit a completion that was read: each buffered job must reach the store exactly once,
        // and every flusher must exit when the writer is completed and drained.
        var store = new ScriptedExecutionStore { Finalized = null };
        var sink = new CompletionSink(
            store,
            new WorkerWakeupPublisher(new WakeupSpy()),
            Options.Create(
                new JobsOptions
                {
                    BatchCompletionSize = 4,
                    BatchCompletionInterval = TimeSpan.FromMilliseconds(20),
                    BatchCompletionMaxBytes = int.MaxValue,
                }
            ),
            new RecordingLogger()
        );

        var flushers = sink.RunFlushersAsync(4);
        for (var jobId = 100L; jobId < 140; jobId++)
        {
            await sink.EnqueueAsync(Buffered(jobId));
        }
        sink.CompleteWriter();
        await flushers.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var committed = store.BatchRequests.Select(r => r.JobId).Order().ToArray();
        Assert.Equal(Enumerable.Range(100, 40).Select(i => (long)i).ToArray(), committed);
    }

    // ---------- helpers ----------

    private static CompletionSink Sink(ScriptedExecutionStore store, WakeupSpy wakes, RecordingLogger log) =>
        new(
            store,
            new WorkerWakeupPublisher(wakes),
            Options.Create(new JobsOptions { BatchCompletionSize = 100, BatchCompletionInterval = TimeSpan.FromMilliseconds(20) }),
            log
        );

    // One drain of exactly the buffered set: the writer is completed before the flusher starts, so the
    // whole batch is read in one pass and the loop exits without waiting out the interval window.
    private static async Task FlushAsync(CompletionSink sink, params BufferedCompletion[] completions)
    {
        foreach (var completion in completions)
        {
            await sink.EnqueueAsync(completion);
        }
        sink.CompleteWriter();
        await sink.RunFlusherAsync();
    }

    private static BufferedCompletion Buffered(long jobId) =>
        new(
            new CompleteExecutionRequest(
                JobId: jobId,
                WorkerId: 1,
                ExpectedExecutionNumber: 1,
                Outcome: ExecutionOutcome.Succeeded,
                ResultFormatId: 0,
                Result: ReadOnlyMemory<byte>.Empty
            ),
            "orders",
            "charge",
            jobId,
            ResultBytes: 0
        );

    private static CompleteExecutionResult Completed(bool parentReleased) =>
        new(CompleteExecutionAction.Completed, (byte)JobStatusCode.Succeeded, null, DateTime.UtcNow, parentReleased);

    // The scripted store: the two calls a flush makes, and nothing else. Finalized null means "the set
    // call finalized everything", which is the happy path the degraded facts are measured against.
    private sealed class ScriptedExecutionStore : IExecutionStore
    {
        private readonly ConcurrentQueue<CompleteExecutionRequest> _batch = new();
        private readonly ConcurrentQueue<CompleteExecutionRequest> _fallback = new();
        private readonly ConcurrentQueue<int> _batchSizes = new();

        public IReadOnlyList<bool>? Finalized { get; init; }
        public Exception? BatchFailure { get; init; }
        public Func<CompleteExecutionRequest, CompleteExecutionResult>? Fallback { get; init; }
        public Action<IReadOnlyList<CompleteExecutionRequest>>? OnBatch { get; set; }

        public IReadOnlyList<CompleteExecutionRequest> BatchRequests => [.. _batch];
        public IReadOnlyList<CompleteExecutionRequest> FallbackRequests => [.. _fallback];
        public IReadOnlyList<int> BatchSizes => [.. _batchSizes];

        public Task<IReadOnlyList<bool>> CompleteExecutionsBatchAsync(
            IReadOnlyList<CompleteExecutionRequest> requests,
            CancellationToken ct
        )
        {
            if (BatchFailure is { } failure)
            {
                return Task.FromException<IReadOnlyList<bool>>(failure);
            }

            foreach (var request in requests)
            {
                _batch.Enqueue(request);
            }
            _batchSizes.Enqueue(requests.Count);
            OnBatch?.Invoke(requests);
            IReadOnlyList<bool> finalized = Finalized ?? [.. requests.Select(_ => true)];
            return Task.FromResult(finalized);
        }

        public Task<CompleteExecutionResult> CompleteExecutionAsync(CompleteExecutionRequest request, CancellationToken ct)
        {
            _fallback.Enqueue(request);
            return Task.FromResult(
                Fallback is null ? throw new NotSupportedException("no fallback scripted for this test") : Fallback(request)
            );
        }

        public Task<ClaimResult> ClaimBatchAsync(ClaimRequest request, int leaseTtlSeconds, CancellationToken ct) =>
            throw new NotSupportedException();

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

        public Task<ReclaimStuckJobsResult> ReclaimStuckJobsAsync(short namespaceId, CancellationToken ct) =>
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
            short namespaceId,
            CancellationToken ct
        ) => throw new NotSupportedException();

        public Task<Acta.Runtime.Modules.Execution.Timers.SleepDecision> ArmOrConsumeSleepTimerAsync(
            ArmOrConsumeSleepTimerCommand command,
            CancellationToken ct
        ) => throw new NotSupportedException();
    }

    private sealed class WakeupSpy : IWorkerWakeup
    {
        private readonly ConcurrentBag<(WorkerWakeupChannel Channel, WorkerWakeupReason Reason)> _wakes = [];

        public IReadOnlyCollection<(WorkerWakeupChannel Channel, WorkerWakeupReason Reason)> Wakes => [.. _wakes];

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            _wakes.Add((channel, reason));
            return ValueTask.CompletedTask;
        }

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
                _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }
        }

        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);
    }
}
