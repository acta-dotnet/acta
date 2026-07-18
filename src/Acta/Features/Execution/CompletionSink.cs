using System.Threading.Channels;
using Acta.Configuration;
using Acta.Features.Execution;
using Acta.Features.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Features.Execution;

/// <summary>One buffered terminal completion plus what the post-flush wakeup needs.</summary>
internal sealed record BufferedCompletion(CompleteExecutionRequest Request, string JobNamespace, long JobId, int ResultBytes);

/// <summary>
/// The <see cref="ExecutionProfile.Bulk"/> completion buffer. Plain terminal completions are written here
/// instead of being committed per job; parallel flusher loops drain them and group-commit each batch via
/// <see cref="Acta.Features.Execution.IExecutionStore.CompleteExecutionsBatchAsync"/> (one set-based round trip, one commit), then publish the
/// deferred wakeups. Rows the set-based routine self-filtered (a parent) fall back to
/// per-job <see cref="Acta.Features.Execution.IExecutionStore.CompleteExecutionAsync"/>. The bounded channel backpressures the claim loop so the
/// buffer cannot grow without limit. A crash loses the unflushed buffer: those jobs stay Executing and
/// <c>sys.recovery</c> re-runs them (at-least-once).
/// </summary>
internal sealed class CompletionSink
{
    private readonly Acta.Features.Execution.IExecutionStore _execution;
    private readonly WorkerWakeupPublisher _wakeupPublisher;
    private readonly int _batchSize;
    private readonly TimeSpan _interval;
    private readonly int _maxBytes;
    private readonly ILogger _log;
    private readonly Channel<BufferedCompletion> _channel;

    public CompletionSink(
        Acta.Features.Execution.IExecutionStore execution,
        WorkerWakeupPublisher wakeupPublisher,
        IOptions<JobsOptions> options,
        ILogger? log = null
    )
    {
        _execution = execution;
        _wakeupPublisher = wakeupPublisher;
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
            var bytes = 0;
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

        try
        {
            var requests = new List<CompleteExecutionRequest>(batch.Count);
            foreach (var b in batch)
            {
                requests.Add(b.Request);
            }

            // One set-based round trip finalizes the simple terminal rows; it self-filters and reports
            // which ordinals it did NOT finalize (a parent, or a lost lease).
            var finalized = await _execution.CompleteExecutionsBatchAsync(requests, CancellationToken.None).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
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
                else
                {
                    // Not finalized in the batch: complete per-job with full semantics (parent child-done
                    // latch) and publish the wakeups the routine reports.
                    var result = await _execution.CompleteExecutionAsync(batch[i].Request, CancellationToken.None).ConfigureAwait(false);
                    await PublishWakeupsAsync(result, batch[i]).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // The whole batch rolled back: every job in it stays Executing, so sys.recovery reclaims and
            // re-runs them. Bulk's at-least-once contract; log and continue with the next batch.
            _log.LogError(ex, "Bulk completion flush of {Count} jobs failed; they remain Executing for recovery.", batch.Count);
        }
    }

    // Buffered completions are always plain terminal landings (Done/Failed), never Ready: publish the
    // job-finished wakeup so a colocated ExecuteAndWaitAsync caller observes the outcome, plus the
    // parent-release wakeup the routine reports. Deferred to flush time (the small
    // extra latency is part of Bulk's relaxed contract).
    private async Task PublishWakeupsAsync(CompleteExecutionResult result, BufferedCompletion b)
    {
        if (result.Action != CompleteExecutionAction.Completed)
        {
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
}
