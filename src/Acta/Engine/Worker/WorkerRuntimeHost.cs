using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// Process-level <see cref="BackgroundService"/>: runs provider bootstrap exactly once and every worker's
/// <see cref="WorkerRuntime.InitializeAsync"/> during startup, then every registered worker's
/// <see cref="WorkerRuntime.RunAsync"/> concurrently under the host stopping token. <see cref="WorkerRuntimeStartup"/>
/// runs in <see cref="StartAsync"/>, before host startup completes, rather than in the background loop, so anything
/// that runs after this service starts (subsequent <c>IHostedService</c>s, a caller that awaited <c>host.StartAsync()</c>)
/// finds the schema and definitions before it enqueues; an enqueue-only process still bootstraps, then idles.
/// </summary>
internal sealed class WorkerRuntimeHost(
    IEnumerable<WorkerRuntime> runtimes,
    IEnumerable<IProviderBootstrap> bootstraps,
    ILogger<WorkerRuntimeHost>? log = null
) : BackgroundService
{
    private static readonly TimeSpan LifecycleStampTimeout = TimeSpan.FromSeconds(5);

    private readonly WorkerRuntime[] _runtimes = runtimes.ToArray();
    private readonly IReadOnlyList<IProviderBootstrap> _bootstraps = bootstraps.ToArray();
    private readonly ILogger _log = log ?? NullLogger<WorkerRuntimeHost>.Instance;

    /// <summary>
    /// Runs <see cref="WorkerRuntimeStartup"/> to completion before host startup finishes, so anything
    /// that starts after (subsequent <c>IHostedService</c>s, a caller that <c>await host.StartAsync()</c>)
    /// can enqueue against this process's definitions immediately. It runs once per process, before any
    /// worker's claim loop, and a failed bootstrap throws out of here before host startup completes.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await WorkerRuntimeStartup.RunAsync(_bootstraps, _runtimes, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runtimes.Length == 0)
        {
            _log.LogInformation("Acta: enqueue-only process; no workers registered.");
            return;
        }

        await Task.WhenAll(_runtimes.Select(r => r.RunAsync(stoppingToken)));
    }

    /// <summary>
    /// Graceful drain on stop: each runtime stops claiming and stamps <c>Draining</c> via the heartbeat, which holds
    /// in-flight leases while handlers run to completion; once every runtime reports no in-flight work the base
    /// <see cref="BackgroundService"/> cancels <see cref="ExecuteAsync"/> and each worker is stamped <c>Stopped</c>. The
    /// in-flight wait is bounded by the host's shutdown token (<c>HostOptions.ShutdownTimeout</c>); past it the base
    /// stop cancels the stragglers and crash recovery reclaims them. The Draining and Stopped writes share a short
    /// timeout on that deadline and a failed one never blocks the others; a hard kill skips this path, and the worker is reaped Dead.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Intake cancellation is synchronous and I/O-free, so every runtime stops claiming before any
        // best-effort lifecycle stamp can stall.
        foreach (var runtime in _runtimes)
        {
            runtime.BeginDrain();
        }

        await RunLifecyclePhaseAsync("begin-drain", static (runtime, ct) => runtime.StampDrainingAsync(ct), cancellationToken);

        await WaitForDrainAsync(cancellationToken);

        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            await RunLifecyclePhaseAsync("clean-shutdown", static (runtime, ct) => runtime.StopAsync(ct), cancellationToken);
        }
    }

    private async Task RunLifecyclePhaseAsync(
        string phase,
        Func<WorkerRuntime, CancellationToken, Task> operation,
        CancellationToken cancellationToken
    )
    {
        var completed = await RunPhaseAsync(
            _runtimes,
            operation,
            LifecycleStampTimeout,
            (runtime, ex) =>
                _log.LogWarning(
                    ex,
                    "Acta: ({Operation}) stamp failed for worker namespace ({Namespace}); lease recovery will reconcile it.",
                    phase,
                    runtime.WorkerNamespaceName
                ),
            cancellationToken
        );
        if (!completed)
        {
            _log.LogWarning("Acta: ({Operation}) stamps exceeded their shutdown budget; continuing shutdown.", phase);
        }
    }

    /// <summary>
    /// Runs a best-effort lifecycle phase over every item at once under one deadline linked to the shutdown token, and
    /// answers whether every item finished in time. A failure reaches <paramref name="onFailure"/> per item, so one
    /// runtime's stamp never blocks another's.
    /// </summary>
    internal static async Task<bool> RunPhaseAsync<T>(
        IReadOnlyCollection<T> items,
        Func<T, CancellationToken, Task> operation,
        TimeSpan timeout,
        Action<T, Exception> onFailure,
        CancellationToken shutdownToken
    )
    {
        using var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        phaseCts.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(items.Select(RunOneAsync)).WaitAsync(phaseCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (phaseCts.IsCancellationRequested)
        {
            // The provider may ignore cancellation. WaitAsync still bounds this shutdown phase; the
            // detached per-item wrapper observes and logs any eventual failure.
            return false;
        }

        async Task RunOneAsync(T item)
        {
            try
            {
                await operation(item, phaseCts.Token);
            }
            catch (OperationCanceledException) when (phaseCts.IsCancellationRequested)
            {
                // The phase deadline is expected and reported once by the caller.
            }
            catch (Exception ex)
            {
                try
                {
                    onFailure(item, ex);
                }
                catch
                {
                    // Logging cannot be allowed to fault a best-effort shutdown phase.
                }
            }
        }
    }

    /// <summary>
    /// Await every runtime's claim/dispatch loop draining its claimed + in-flight work, bounded by
    /// the host shutdown token. Awaiting the loop (not a sampled in-flight count) so
    /// channel-buffered claims are run too. A drain that does not finish within
    /// HostOptions.ShutdownTimeout cancels the token; the base stop then ends the stragglers and
    /// crash recovery reclaims them.
    /// </summary>
    private async Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(_runtimes.Select(r => r.DrainCompletion)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown budget elapsed before the drain finished; the base stop cancels in-flight work.
        }
        catch (Exception ex)
        {
            // A loop faulted; base.StopAsync surfaces it. Don't let it abort the rest of the shutdown.
            _log.LogWarning(ex, "Acta: a worker loop faulted during drain; continuing shutdown.");
        }
    }
}
