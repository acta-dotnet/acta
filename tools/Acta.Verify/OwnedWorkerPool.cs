namespace Acta.Verify;

internal sealed class OwnedWorkerPool : IDisposable
{
    private readonly List<OwnedWorker> _workers = [];
    public IReadOnlyList<int> ProcessIds => _workers.Select(w => w.Id).ToArray();
    public int LiveCount => _workers.Count(w => w.IsRunning);

    /// <summary>The settings the first started worker reported; every worker in the pool starts with the same overrides.</summary>
    public WorkerSettings? Settings { get; private set; }

    /// <summary>Starts workers on the product defaults, overriding only an executor count a diagnostic cell names.</summary>
    public async Task StartAsync(
        OwnedProvider provider,
        EffectService? oracle,
        int workers,
        int? executors,
        CancellationToken ct,
        int namespaces = 1,
        bool crossNamespace = false
    )
    {
        for (var i = 0; i < workers; i++)
        {
            var worker = OwnedWorker.Start(
                provider,
                oracle,
                executors: executors,
                heartbeatSeconds: null,
                jobNamespace: crossNamespace ? "bench-cross"
                    : i % namespaces == 0 ? "bench"
                    : "bench-2"
            );
            _workers.Add(worker);
        }
        await Task.WhenAll(_workers.Select(w => w.ReadyAsync(ct)));
        Settings = _workers[0].Settings;
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
