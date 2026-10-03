namespace Acta.Runtime;

/// <summary><see cref="IWorkerLifecycle"/>, one line per operation.</summary>
internal sealed class WorkerLifecycle(
    StartWorkerHandler startWorker,
    StopWorkerHandler stopWorker,
    ExtendWorkerLeasesHandler extendWorkerLeases,
    MarkDeadWorkersHandler markDeadWorkers
) : IWorkerLifecycle
{
    public Task<StartWorkerRow> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) => startWorker.HandleAsync(command, ct);

    public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) =>
        stopWorker.HandleAsync(new StopWorkerCommand(namespaceId, workerId), ct);

    public Task<IReadOnlyList<LeaseRenewalRow>> ExtendWorkerLeasesAsync(
        int workerId,
        int leaseTtlSeconds,
        bool draining,
        CancellationToken ct
    ) => extendWorkerLeases.HandleAsync(new ExtendWorkerLeasesCommand(workerId, leaseTtlSeconds, draining), ct);

    public Task<int> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) =>
        markDeadWorkers.HandleAsync(new MarkDeadWorkersCommand(deadAfterSeconds), ct);
}
