using System.Threading.Channels;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution;

/// <summary>One buffered terminal completion plus what the post-flush wakeup and metric need.</summary>
internal sealed record BufferedCompletion(
    CompleteExecutionRequest Request,
    string JobNamespace,
    string JobName,
    long JobId,
    int ResultBytes
);

/// <summary>
/// The <see cref="ExecutionProfile.Bulk"/> completion buffer. Plain terminal completions are written here
/// instead of being committed per job; parallel flusher loops drain them and group-commit each batch via
/// <see cref="Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionsBatchAsync"/> (one set-based round trip, one commit), then publish the
/// deferred wakeups. Rows the set-based routine self-filtered (a parent) fall back to
/// per-job <see cref="Acta.Runtime.Modules.Execution.IExecutionStore.CompleteExecutionAsync"/>. The bounded channel backpressures the claim loop so the
/// buffer cannot grow without limit. A crash loses the unflushed buffer: those jobs stay Executing and
/// <c>sys.recovery</c> re-runs them (at-least-once). A flush is not all-or-nothing past the set call:
/// the set-based commit and each per-job fallback are separate transactions, so a mid-flush failure
/// leaves the already-committed rows terminal and only the rest for recovery, and the log names the
/// jobs that are actually unfinalized rather than the whole batch.
/// </summary>
internal sealed class CompletionSink
{
    // Per-job completions a flush issues at once. Four rather than the whole batch, because a flusher
    // stands in for a handful of executors and should not open a connection per buffered row; four
    // rather than one, because an entry whose completion keeps failing must not be able to hold its
    // siblings up, and at four it cannot even when several entries are failing at once.
    private const int FallbackParallelism = 4;

    private readonly Acta.Runtime.Modules.Execution.IExecutionStore _execution;
    private readonly WorkerWakeupPublisher _wakeupPublisher;
    private readonly int _batchSize;
    private readonly TimeSpan _interval;
    private readonly int _maxBytes;
    private readonly ILogger _log;
    private readonly JobMetrics? _metrics;
    private readonly Channel<BufferedCompletion> _channel;

    public CompletionSink(
        Acta.Runtime.Modules.Execution.IExecutionStore execution,
        WorkerWakeupPublisher wakeupPublisher,
        IOptions<JobsOptions> options,
        ILogger? log = null,
        JobMetrics? metrics = null
    )
    {
        _execution = execution;
        _wakeupPublisher = wakeupPublisher;
        _metrics = metrics;
        var o = options.Value;
        _batchSize = Math.Max(1, o.BatchCompletionSize);
        _interval = o.BatchCompletionInterval;
        _maxBytes = Math.Max(1, o.BatchCompletionMaxBytes);
        _log = log ?? NullLogger.Instance;
        _channel = Channel.CreateBounded<BufferedCompletion>(
            new BoundedChannelOptions(Math.Max(2, _batchSize * 2))
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );
    }

    /// <summary>
    /// Buffers a completed job. Uses an uncancellable write: the handler already ran, so the completion
    /// must not be dropped on shutdown; backpressure (a full channel) just delays the executor's next claim.
    /// </summary>
    public ValueTask EnqueueAsync(BufferedCompletion completion) => _channel.Writer.WriteAsync(completion, CancellationToken.None);

    /// <summary>Signals no more completions will be buffered, so the flushers drain and exit.</summary>
    public void CompleteWriter() => _channel.Writer.TryComplete();

    /// <summary>
    /// Runs <paramref name="parallelism"/> concurrent flusher loops over the shared (multi-reader) buffer.
    /// One flusher serializes its round-trips, so several run in parallel to keep completion throughput up
    /// while each still group-commits its own batches. Each exits when the writer is completed and drained.
    /// </summary>
    public Task RunFlushersAsync(int parallelism, CancellationToken stopCt)
    {
        var n = Math.Max(1, parallelism);
        var flushers = new Task[n];
        for (var i = 0; i < n; i++)
        {
            flushers[i] = Task.Run(() => RunFlusherAsync(stopCt), CancellationToken.None);
        }

        return Task.WhenAll(flushers);
    }

    /// <summary>
    /// Drains the buffer until the writer is completed, group-committing each batch on a size, byte, or
    /// time trigger. Started by <see cref="WorkerLoop"/> in the Bulk branch; stops when
    /// <see cref="CompleteWriter"/> is called after the dispatch loop has drained its in-flight handlers.
    /// <paramref name="stopCt"/> is the worker's host token: it bounds only how long a failing completion
    /// is repeated, never the reading or the writes themselves, and a graceful drain leaves it live so the
    /// buffer still lands.
    /// </summary>
    public async Task RunFlusherAsync(CancellationToken stopCt)
    {
        var reader = _channel.Reader;
        var buffer = new List<BufferedCompletion>(_batchSize);
        // No token on the read: a hard stop must still let the already-buffered completions be written,
        // because their handlers ran. stopCt only ends a repeat that cannot land.
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            buffer.Clear();
            // Long accumulator: a batch of near-int-max results must trip the byte threshold, never
            // wrap negative and disable it (each item is int-bounded, so the long sum cannot overflow).
            var bytes = 0L;
            using var window = new CancellationTokenSource(_interval);
            while (buffer.Count < _batchSize && bytes < _maxBytes)
            {
                if (reader.TryRead(out var item))
                {
                    buffer.Add(item);
                    bytes += item.ResultBytes;
                    continue;
                }

                try
                {
                    var next = await reader.ReadAsync(window.Token).ConfigureAwait(false);
                    buffer.Add(next);
                    bytes += next.ResultBytes;
                }
                catch (OperationCanceledException)
                {
                    break; // interval window elapsed: flush the partial batch
                }
                catch (ChannelClosedException)
                {
                    break; // writer completed and channel drained
                }
            }

            await FlushAsync(buffer, stopCt).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(List<BufferedCompletion> batch, CancellationToken stopCt)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // Order the flush by job id so the batch locks rows in the same order the worker heartbeat does
        // and the two cannot deadlock on an overlapping set. Ordinals are positions in this list, so
        // sorting before they are assigned keeps each result aligned with its own completion.
        batch.Sort(static (x, y) => x.Request.JobId.CompareTo(y.Request.JobId));

        var requests = new List<CompleteExecutionRequest>(batch.Count);
        foreach (var b in batch)
        {
            requests.Add(b.Request);
        }

        IReadOnlyList<bool> finalized;
        try
        {
            // One set-based round trip finalizes the simple terminal rows; it self-filters and reports
            // which ordinals it did NOT finalize (a parent, or a lost lease). The write itself is
            // uncancellable because the handlers already ran; the stop token bounds only the waits
            // between tries, so a repeat that cannot land does not outlive the process it belongs to.
            (finalized, _) = await CompletionWrite
                .RetryAsync(
                    _ => _execution.CompleteExecutionsBatchAsync(requests, CancellationToken.None),
                    _log,
                    batch[0].JobId,
                    stopCt,
                    _metrics
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One statement, one commit, so nothing landed, and the worker stopped before the repeat could
            // land it: every job in the batch stays Executing under the lease this worker no longer renews,
            // and sys.recovery reclaims them once it lapses. Log and take the next batch. This is the only
            // path that may claim the whole batch rolled back.
            _log.LogError(ex, "Bulk completion flush of {Count} jobs failed; they remain Executing for recovery.", batch.Count);
            return;
        }

        // Past the set call its finalized rows are committed, so each remaining step stands on its own:
        // one failure must not strand the rows after it, and must not be reported as a rollback. The
        // entries settle side by side because every handler in this batch has already run: a per-job
        // completion that keeps failing is repeated until the worker stops, and run in sequence it would
        // hold the rest of the batch's completions, and every deferred wakeup, behind it for that time.
        //
        // The gate is not disposed: nothing ever touches its WaitHandle, so it holds no unmanaged
        // resource, and every task that captured it has completed by the time the flush returns anyway.
        var gate = new SemaphoreSlim(FallbackParallelism, FallbackParallelism);
        var settling = new Task<FlushOutcome>[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            settling[i] = SettleAsync(batch[i], finalized[i], gate, stopCt);
        }

        var settled = await Task.WhenAll(settling).ConfigureAwait(false);

        // Reported in batch order rather than in the order the entries landed, so the same failure reads
        // the same way on every flush.
        List<long>? unresolved = null;
        for (var i = 0; i < batch.Count; i++)
        {
            if (settled[i].CompletionFailure is not null)
            {
                (unresolved ??= []).Add(batch[i].JobId);
            }
        }

        var completionFailure = Array.Find(settled, o => o.CompletionFailure is not null).CompletionFailure;
        var wakeFailure = Array.Find(settled, o => o.WakeFailure is not null).WakeFailure;

        if (unresolved is not null)
        {
            _log.LogError(
                completionFailure,
                "Bulk completion left {Count} jobs unfinalized ({Detail}); those remain Executing for recovery.",
                unresolved.Count,
                $"of {batch.Count} in the batch: {string.Join(", ", unresolved)}"
            );
        }

        if (wakeFailure is not null)
        {
            _log.LogWarning(
                wakeFailure,
                "Bulk completion finalized its jobs but at least one of {Count} wakeups failed; a waiting caller observes the outcome by poll instead.",
                batch.Count
            );
        }
    }

    /// <summary>What one entry of a flush left for the report: at most one failure on each of its steps.</summary>
    private readonly record struct FlushOutcome(Exception? CompletionFailure, Exception? WakeFailure);

    /// <summary>
    /// Finalizes one entry of a flush and then publishes its wakeup, in that order so a failed wakeup is
    /// never mistaken for an unfinalized job. Never throws: a failure on either step is the entry's own
    /// and is carried back for the batch-level report.
    /// </summary>
    private async Task<FlushOutcome> SettleAsync(BufferedCompletion entry, bool alreadyFinalized, SemaphoreSlim gate, CancellationToken stopCt)
    {
        CompleteExecutionResult? result = null;
        Exception? completionFailure = null;

        if (alreadyFinalized)
        {
            RecordDurableCompletion(entry);
        }
        else
        {
            try
            {
                // The gate is held for the round trip alone and released before the repeat's backoff wait,
                // so an entry that keeps failing sits outside it almost all of the time and its siblings
                // keep flowing. Without one, a batch of self-filtered rows would open a connection per row.
                (result, _) = await WriteThroughGateAsync(
                        gate,
                        token => _execution.CompleteExecutionAsync(entry.Request, token),
                        entry.JobId,
                        stopCt
                    )
                    .ConfigureAwait(false);
                if (result is { Action: CompleteExecutionAction.Completed })
                {
                    RecordDurableCompletion(entry);
                }
            }
            catch (Exception ex)
            {
                completionFailure = ex;
            }
        }

        Exception? wakeFailure = null;
        try
        {
            if (alreadyFinalized)
            {
                // Finalized simple terminal: no parent latch by construction, so only the job-finished
                // wakeup applies (for a colocated RunAndWaitAsync caller).
                await _wakeupPublisher
                    .WakeAsync(WorkerWakeupChannel.JobCompletion(entry.JobId), WorkerWakeupReason.JobFinished, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else if (result is { } r)
            {
                await PublishWakeupsAsync(r, entry).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            wakeFailure = ex;
        }

        return new FlushOutcome(completionFailure, wakeFailure);
    }

    /// <summary>
    /// One completion round trip, admitted through <paramref name="gate"/> and repeated until it lands or
    /// the worker stops. The write itself is uncancellable because the handler already ran; the stop token
    /// bounds only the waits between tries.
    /// </summary>
    private async Task<(CompleteExecutionResult Result, bool Retried)> WriteThroughGateAsync(
        SemaphoreSlim gate,
        Func<CancellationToken, Task<CompleteExecutionResult>> write,
        long jobId,
        CancellationToken stopCt
    ) =>
        await CompletionWrite
            .RetryAsync(
                async _ =>
                {
                    await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        return await write(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                },
                _log,
                jobId,
                stopCt,
                _metrics
            )
            .ConfigureAwait(false);

    /// <summary>
    /// Buffered completions are always plain terminal landings (Succeeded/Failed), never Ready:
    /// publish the job-finished wakeup so a colocated RunAndWaitAsync caller observes the outcome,
    /// plus the parent-release wakeup the routine reports. Deferred to flush time (the small extra
    /// latency is part of Bulk's relaxed contract).
    /// </summary>
    private async Task PublishWakeupsAsync(CompleteExecutionResult result, BufferedCompletion b)
    {
        if (result.Action != CompleteExecutionAction.Completed)
        {
            // The per-job CAS matched nothing: an external control or a reclaim moved the row while
            // the completion sat buffered. Nothing was finalized here; recovery or the concurrent
            // winner owns the row now. Say so, or the buffered completion vanishes without a trace.
            _log.LogWarning(
                "Bulk fallback completion for job {JobId} returned ({Outcome}); nothing was finalized here.",
                b.JobId,
                result.Action.ToString()
            );
            return;
        }

        if (result.FinalStatusCode is { } finalStatus && ((JobStatusCode)finalStatus).IsTerminal)
        {
            await _wakeupPublisher
                .WakeAsync(WorkerWakeupChannel.JobCompletion(b.JobId), WorkerWakeupReason.JobFinished, CancellationToken.None)
                .ConfigureAwait(false);
        }

        if (result.ParentReleased)
        {
            await _wakeupPublisher
                .WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The Bulk execution metric is recorded here, at durable finalization, not at handler finish:
    /// a buffered completion can still lose its CAS or fail to flush, and "acta.executions" must
    /// count what the store confirmed, matching the Direct/Buffered post-CAS semantics.
    /// </summary>
    private void RecordDurableCompletion(BufferedCompletion b) =>
        _metrics?.RecordExecution(
            b.JobNamespace,
            b.JobName,
            JobExecution.OutcomeTag(b.Request.Outcome),
            b.Request.JobEventReasonCode?.Code,
            b.Request.DurationMs ?? 0
        );
}
