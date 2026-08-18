using Acta.Runtime.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Process-level <see cref="BackgroundService"/>. Runs provider bootstrap exactly once (migrations /
/// schema) and every worker's <see cref="WorkerRuntime.InitializeAsync"/> during startup, then runs
/// every registered worker's <see cref="WorkerRuntime.RunAsync"/> concurrently under the host
/// stopping token.
/// </summary>
/// <remarks>
/// Bootstrap and worker initialization (catalog upsert: namespace + definitions + <c>workers</c>)
/// run in <see cref="StartAsync"/> (once per process, before host startup completes) rather than
/// inside the background loop, so a multi-worker process migrates the schema a single time AND
/// anything that runs after this service starts (subsequent <c>IHostedService</c>s, a caller that
/// <c>await host.StartAsync()</c>) is guaranteed the schema and this process's definitions exist
/// before it enqueues (no startup race). In production (<c>ApplyMigrationsOnStartup</c> off) the
/// bootstraps no-op cheaply, so startup stays fast. An enqueue-only process (no <c>j.Run(...)</c> call)
/// registers no workers: bootstrap still runs so the catalog exists, then the host idles until shutdown.
/// </remarks>
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
    /// Runs provider bootstrap (migrations / schema) and every worker's catalog initialization to
    /// completion before host startup finishes, so anything that starts after (subsequent
    /// <c>IHostedService</c>s, a caller that <c>await host.StartAsync()</c>) can enqueue against this
    /// process's definitions immediately. Both run once per process, before any worker's claim loop.
    /// In production (<c>ApplyMigrationsOnStartup</c> off) the bootstraps no-op cheaply.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var bootstrap in _bootstraps)
        {
            await bootstrap.RunAsync(cancellationToken);
        }

        foreach (var runtime in _runtimes)
        {
            await runtime.InitializeAsync(cancellationToken);
        }

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
    /// Graceful drain on stop: each runtime stops claiming new work and stamps <c>Draining</c> (via the
    /// heartbeat), the heartbeat holds in-flight leases while handlers run to completion, then once every
    /// runtime reports no in-flight work the base <see cref="BackgroundService"/> cancels
    /// <see cref="ExecuteAsync"/> and each worker is stamped <c>Stopped</c>. The in-flight wait is bounded by
    /// the host's shutdown token (<c>HostOptions.ShutdownTimeout</c>); if it elapses, the base stop cancels
    /// the stragglers and <c>sys.recovery</c> reclaims them. Draining and Stopped writes run concurrently
    /// under a short timeout linked to that same host deadline; a per-runtime failure is logged and never
    /// blocks the others. A hard kill skips this path entirely, and <c>mark_dead_workers</c> then reaps the
    /// worker as Dead.
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
        var completed = await WorkerShutdownPhase.RunAsync(
            _runtimes,
            operation,
            LifecycleStampTimeout,
            (runtime, ex) =>
                _log.LogWarning(
                    ex,
                    "Acta: {Operation} stamp failed for worker namespace {Namespace}; lease recovery will reconcile it.",
                    phase,
                    runtime.WorkerNamespaceName
                ),
            cancellationToken
        );
        if (!completed)
        {
            _log.LogWarning("Acta: {Operation} stamps exceeded their shutdown budget; continuing shutdown.", phase);
        }
    }

    // Await every runtime's claim/dispatch loop draining its claimed + in-flight work, bounded by the host
    // shutdown token. Awaiting the loop (not a sampled in-flight count) so channel-buffered claims are run
    // too. A drain that does not finish within HostOptions.ShutdownTimeout cancels the token; the base stop
    // then ends the stragglers and sys.recovery reclaims them.
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
