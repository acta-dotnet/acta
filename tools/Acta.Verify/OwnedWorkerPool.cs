using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Acta.Verify;

internal sealed class OwnedWorkerPool : IDisposable
{
    private readonly List<OwnedWorker> _workers = [];
    private IHostApplicationLifetime? _inProcess;
    public IReadOnlyList<int> ProcessIds => _workers.Select(w => w.Id).ToArray();
    public int LiveCount =>
        _workers.Count(w => w.IsRunning) + (_inProcess is { ApplicationStopping.IsCancellationRequested: false } ? 1 : 0);

    /// <summary>The settings the first started worker reported; every worker in the pool starts with the same overrides.</summary>
    public WorkerSettings? Settings { get; private set; }

    /// <summary>Starts workers on the product defaults, overriding only the executor count a scenario writes.</summary>
    public async Task StartAsync(
        OwnedProvider provider,
        EffectService? effects,
        int workers,
        int? executors,
        CancellationToken ct,
        int namespaces = 1,
        bool crossNamespace = false,
        string? redis = null
    )
    {
        for (var i = 0; i < workers; i++)
        {
            var worker = OwnedWorker.Start(
                provider,
                effects,
                executors: executors,
                heartbeatSeconds: null,
                jobNamespace: crossNamespace ? "verify-cross"
                    : i % namespaces == 0 ? "verify"
                    : "verify-2",
                redis: redis
            );
            _workers.Add(worker);
        }
        await Task.WhenAll(_workers.Select(w => w.ReadyAsync(ct)));
        Settings = _workers[0].Settings;
    }

    /// <summary>
    /// Counts the controller's own host as the trial's one worker, with no process of its own: its settings, and its
    /// liveness until the host starts stopping, which a faulted worker pipeline makes it do. The trial disposes the host.
    /// </summary>
    public void AdoptInProcess(IHost host)
    {
        _inProcess = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Settings = WorkerSettings.From(host.Services.GetRequiredService<IOptions<JobsOptions>>().Value);
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
        {
            worker.Dispose();
        }
        _workers.Clear();
    }
}
