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
    public Task RunFlushersAsync(int parallelism)
    {
        var n = Math.Max(1, parallelism);
        var flushers = new Task[n];
        for (var i = 0; i < n; i++)
        {
            flushers[i] = Task.Run(RunFlusherAsync);
        }

        return Task.WhenAll(flushers);
    }

    /// <summary>
    /// Drains the buffer until the writer is completed, group-committing each batch on a size, byte, or
    /// time trigger. Started by <see cref="WorkerLoop"/> in the Bulk branch; stops when
    /// <see cref="CompleteWriter"/> is called after the dispatch loop has drained its in-flight handlers.
    /// </summary>
    public async Task RunFlusherAsync()
    {
        var reader = _channel.Reader;
        var buffer = new List<BufferedCompletion>(_batchSize);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
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

            await FlushAsync(buffer).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(List<BufferedCompletion> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var requests = new List<CompleteExecutionRequest>(batch.Count);
        foreach (var b in batch)
        {
            requests.Add(b.Request);
        }

        IReadOnlyList<bool> finalized;
        try
        {
            // One set-based round trip finalizes the simple terminal rows; it self-filters and reports
            // which ordinals it did NOT finalize (a parent, or a lost lease).
            finalized = await _execution.CompleteExecutionsBatchAsync(requests, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // One statement, one commit, so nothing landed: every job in the batch stays Executing and
            // sys.recovery reclaims them. Bulk's at-least-once contract; log and take the next batch.
            // This is the only path that may claim the whole batch rolled back.
            _log.LogError(ex, "Bulk completion flush of {Count} jobs failed; they remain Executing for recovery.", batch.Count);
            return;
        }

        // Past the set call its finalized rows are committed, so each remaining step stands on its own:
        // one failure must not strand the rows after it, and must not be reported as a rollback. Finalize
        // first, then notify, so a failed wakeup is never mistaken for an unfinalized job.
        var results = new CompleteExecutionResult?[batch.Count];
        List<long>? unresolved = null;
        Exception? completionFailure = null;
        for (var i = 0; i < batch.Count; i++)
        {
            if (finalized[i])
            {
                RecordDurableCompletion(batch[i]);
                continue;
            }

            try
            {
                // Not finalized in the batch: complete per-job with full semantics (parent child-done latch).
                results[i] = await _execution.CompleteExecutionAsync(batch[i].Request, CancellationToken.None).ConfigureAwait(false);
                if (results[i] is { Action: CompleteExecutionAction.Completed })
                {
                    RecordDurableCompletion(batch[i]);
                }
            }
            catch (Exception ex)
            {
                completionFailure ??= ex;
                (unresolved ??= []).Add(batch[i].JobId);
            }
        }

        Exception? wakeFailure = null;
        for (var i = 0; i < batch.Count; i++)
        {
            try
            {
                if (finalized[i])
                {
                    // Finalized simple terminal: no parent latch by construction, so only the
                    // job-finished wakeup applies (for a colocated ExecuteAndWaitAsync caller).
                    await _wakeupPublisher
                        .WakeAsync(
                            WorkerWakeupChannel.JobCompletion(batch[i].JobId),
                            WorkerWakeupReason.JobFinished,
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
                else if (results[i] is { } result)
                {
                    // Publish the wakeups the per-job routine reported.
                    await PublishWakeupsAsync(result, batch[i]).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                wakeFailure ??= ex;
            }
        }

        if (unresolved is not null)
        {
            _log.LogError(
                completionFailure,
                "Bulk completion left {Unresolved} of {Count} jobs unfinalized ({JobIds}); those remain Executing for recovery.",
                unresolved.Count,
                batch.Count,
                string.Join(", ", unresolved)
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

    // Buffered completions are always plain terminal landings (Succeeded/Failed), never Ready: publish the
    // job-finished wakeup so a colocated ExecuteAndWaitAsync caller observes the outcome, plus the
    // parent-release wakeup the routine reports. Deferred to flush time (the small
    // extra latency is part of Bulk's relaxed contract).
    private async Task PublishWakeupsAsync(CompleteExecutionResult result, BufferedCompletion b)
    {
        if (result.Action != CompleteExecutionAction.Completed)
        {
            // The per-job CAS matched nothing: an external control or a reclaim moved the row while
            // the completion sat buffered. Nothing was finalized here; recovery or the concurrent
            // winner owns the row now. Say so, or the buffered completion vanishes without a trace.
            _log.LogWarning(
                "Bulk fallback completion for job {JobId} returned {Action}; nothing was finalized here.",
                b.JobId,
                result.Action
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

    // The Bulk execution metric is recorded here, at durable finalization, not at handler finish:
    // a buffered completion can still lose its CAS or fail to flush, and "acta.executions" must
    // count what the store confirmed, matching the Direct/Buffered post-CAS semantics.
    private void RecordDurableCompletion(BufferedCompletion b) =>
        _metrics?.RecordExecution(
            b.JobNamespace,
            b.JobName,
            JobExecution.OutcomeTag(b.Request.Outcome),
            b.Request.JobEventReasonCode?.Code,
            b.Request.DurationMs ?? 0
        );
}
