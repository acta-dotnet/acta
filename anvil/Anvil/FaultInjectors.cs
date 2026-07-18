using System.Diagnostics;
using Acta;

namespace Anvil;

/// <summary>Two explicit, independently cancellable faults for the live lab.</summary>
public sealed class FaultInjectors(WorkerProcessLauncher launcher, AnvilSession session, IServiceScopeFactory scopes) : IDisposable
{
    private const int PressureChunkSize = 5_000;
    private readonly object _gate = new();
    private CancellationTokenSource? _continuousCrashesCts;
    private CancellationTokenSource? _queuePressureCts;
    private int _workersCrashed;
    private int _queuePressureRate;
    private long _pressureJobsAdded;
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
        lock (_gate)
        {
            crashes = _continuousCrashesCts;
            pressure = _queuePressureCts;
            _continuousCrashesCts = null;
            _queuePressureCts = null;
            _queuePressureRate = 0;
        }
        crashes?.Cancel();
        pressure?.Cancel();
    }
}

public sealed record FaultSnapshot(
    bool ContinuousCrashesActive,
    int WorkersCrashed,
    bool QueuePressureActive,
    int QueuePressureRate,
    long PressureJobsAdded,
    string? LastError
);
