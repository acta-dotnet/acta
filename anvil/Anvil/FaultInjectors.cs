using System.Diagnostics;
using Acta;

namespace Anvil;

/// <summary>Three explicit, independently cancellable faults for the live lab.</summary>
public sealed class FaultInjectors(
    WorkerProcessLauncher launcher,
    AnvilSession session,
    IServiceScopeFactory scopes,
    AnvilOutboxDatabase outboxDb
) : IDisposable
{
    private const int PressureChunkSize = 5_000;

    // Smaller than direct-enqueue chunks: each chunk is one producer-file write transaction, and the
    // relay needs regular gaps to claim between them on the shared SQLite write lock.
    private const int OutboxChunkSize = 500;
    private readonly Lock _gate = new();

    // Each fault's source is minted by its Start* verb and handed to the background loop that verb
    // launches (RunContinuousCrashesAsync / RunQueuePressureAsync / RunOutboxPressureAsync); that loop
    // owns it from then on and disposes it in its own finally once it unwinds. Stop* and Dispose only
    // Cancel, which is what actually gets the loop there: the loop is still awaiting Token when they
    // run, so disposing the source from here would race that read and throw ObjectDisposedException
    // instead of stopping the fault.
    private CancellationTokenSource? _continuousCrashesCts;
    private CancellationTokenSource? _queuePressureCts;
    private CancellationTokenSource? _outboxPressureCts;
    private int _workersCrashed;
    private int _queuePressureRate;
    private int _outboxPressureRate;
    private long _pressureJobsAdded;
    private long _outboxRowsStaged;
    private string? _lastError;

    public string StartContinuousCrashes()
    {
        lock (_gate)
        {
            if (_continuousCrashesCts is not null)
            {
                return "Already running.";
            }

            var cts = new CancellationTokenSource();
            _continuousCrashesCts = cts;
            _lastError = null;
            _ = Task.Run(() => RunContinuousCrashesAsync(cts));
            return "Continuous crashes started.";
        }
    }

    public string StopContinuousCrashes()
    {
        CancellationTokenSource owner;
        lock (_gate)
        {
            if (_continuousCrashesCts is null)
            {
                return "Already stopped.";
            }

            owner = _continuousCrashesCts;
            _continuousCrashesCts = null;
        }
        owner.Cancel();
        return "Continuous crashes stopped.";
    }

    public string StartQueuePressure(int jobsPerSecond)
    {
        if (jobsPerSecond is not (1_000 or 10_000))
        {
            throw new ArgumentOutOfRangeException(nameof(jobsPerSecond), "Queue pressure supports 1,000 or 10,000 jobs per second.");
        }

        lock (_gate)
        {
            if (_queuePressureCts is not null)
            {
                return "Already running.";
            }

            var cts = new CancellationTokenSource();
            _queuePressureCts = cts;
            _queuePressureRate = jobsPerSecond;
            _lastError = null;
            _ = Task.Run(() => RunQueuePressureAsync(cts, jobsPerSecond));
            return "Queue pressure started.";
        }
    }

    public string StopQueuePressure()
    {
        CancellationTokenSource owner;
        lock (_gate)
        {
            if (_queuePressureCts is null)
            {
                return "Already stopped.";
            }

            owner = _queuePressureCts;
            _queuePressureCts = null;
            _queuePressureRate = 0;
        }
        owner.Cancel();
        return "Queue pressure stopped.";
    }

    public string StartOutboxPressure(int rowsPerSecond)
    {
        if (rowsPerSecond is not (1_000 or 10_000))
        {
            throw new ArgumentOutOfRangeException(nameof(rowsPerSecond), "Outbox pressure supports 1,000 or 10,000 rows per second.");
        }

        lock (_gate)
        {
            if (_outboxPressureCts is not null)
            {
                return "Already running.";
            }

            var cts = new CancellationTokenSource();
            _outboxPressureCts = cts;
            _outboxPressureRate = rowsPerSecond;
            _lastError = null;
            _ = Task.Run(() => RunOutboxPressureAsync(cts, rowsPerSecond));
            return "Outbox pressure started.";
        }
    }

    public string StopOutboxPressure()
    {
        CancellationTokenSource owner;
        lock (_gate)
        {
            if (_outboxPressureCts is null)
            {
                return "Already stopped.";
            }

            owner = _outboxPressureCts;
            _outboxPressureCts = null;
            _outboxPressureRate = 0;
        }
        owner.Cancel();
        return "Outbox pressure stopped.";
    }

    public FaultSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new FaultSnapshot(
                ContinuousCrashesActive: _continuousCrashesCts is not null,
                WorkersCrashed: _workersCrashed,
                QueuePressureActive: _queuePressureCts is not null,
                QueuePressureRate: _queuePressureRate,
                PressureJobsAdded: _pressureJobsAdded,
                OutboxPressureActive: _outboxPressureCts is not null,
                OutboxPressureRate: _outboxPressureRate,
                OutboxRowsStaged: _outboxRowsStaged,
                LastError: _lastError
            );
        }
    }

    private async Task RunContinuousCrashesAsync(CancellationTokenSource owner)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), owner.Token);
                if (launcher.CrashOneHealthy() is null)
                {
                    continue;
                }

                lock (_gate)
                {
                    _workersCrashed++;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(750), owner.Token);
                launcher.Spawn();
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RecordError(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_continuousCrashesCts, owner))
                {
                    _continuousCrashesCts = null;
                }
            }
            owner.Dispose();
        }
    }

    private async Task RunQueuePressureAsync(CancellationTokenSource owner, int jobsPerSecond)
    {
        try
        {
            while (true)
            {
                owner.Token.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                await SeedPressureSecondAsync(jobsPerSecond, owner.Token);
                var remaining = TimeSpan.FromSeconds(1) - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, owner.Token);
                }
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RecordError(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_queuePressureCts, owner))
                {
                    _queuePressureCts = null;
                    _queuePressureRate = 0;
                }
            }
            owner.Dispose();
        }
    }

    private async Task SeedPressureSecondAsync(int jobsPerSecond, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobs>();
        var batch = session.NextBatch();
        var processed = 0;

        while (processed < jobsPerSecond)
        {
            var count = Math.Min(PressureChunkSize, jobsPerSecond - processed);
            var requests = new List<JobEnqueueRequest>(count);
            for (var i = 0; i < count; i++)
            {
                var index = processed + i;
                requests.Add(
                    AnvilSeeder.Request(
                        session.NamespaceName,
                        session.RunId,
                        batch,
                        "noop",
                        index,
                        AnvilPayloads.Json(new NoOp($"pressure-{batch}-{index}")),
                        AnvilWorkloadCode.NoOp
                    )
                );
            }

            var outcomes = await jobs.EnqueueBatchAsync(requests, ct);
            lock (_gate)
            {
                _pressureJobsAdded += outcomes.Count;
            }
            processed += count;
        }
    }

    private async Task RunOutboxPressureAsync(CancellationTokenSource owner, int rowsPerSecond)
    {
        try
        {
            while (true)
            {
                owner.Token.ThrowIfCancellationRequested();
                var stopwatch = Stopwatch.StartNew();
                var batch = session.NextBatch();
                var staged = 0;
                while (staged < rowsPerSecond)
                {
                    var count = Math.Min(OutboxChunkSize, rowsPerSecond - staged);
                    await outboxDb.StageAsync(count, batch, firstOrdinal: staged, owner.Token);
                    lock (_gate)
                    {
                        _outboxRowsStaged += count;
                    }
                    staged += count;
                }

                var remaining = TimeSpan.FromSeconds(1) - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, owner.Token);
                }
            }
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
        catch (Exception ex)
        {
            RecordError(ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_outboxPressureCts, owner))
                {
                    _outboxPressureCts = null;
                    _outboxPressureRate = 0;
                }
            }
            owner.Dispose();
        }
    }

    private void RecordError(Exception ex)
    {
        var line = ex.Message.Split('\n', '\r')[0].Trim();
        lock (_gate)
        {
            _lastError = line.Length > 160 ? line[..160] : line;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? crashes;
        CancellationTokenSource? pressure;
        CancellationTokenSource? outbox;
        lock (_gate)
        {
            crashes = _continuousCrashesCts;
            pressure = _queuePressureCts;
            outbox = _outboxPressureCts;
            _continuousCrashesCts = null;
            _queuePressureCts = null;
            _queuePressureRate = 0;
            _outboxPressureCts = null;
            _outboxPressureRate = 0;
        }
        crashes?.Cancel();
        pressure?.Cancel();
        outbox?.Cancel();
    }
}

public sealed record FaultSnapshot(
    bool ContinuousCrashesActive,
    int WorkersCrashed,
    bool QueuePressureActive,
    int QueuePressureRate,
    long PressureJobsAdded,
    bool OutboxPressureActive,
    int OutboxPressureRate,
    long OutboxRowsStaged,
    string? LastError
);
