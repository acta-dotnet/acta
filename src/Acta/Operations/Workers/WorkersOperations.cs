namespace Acta;

/// <summary><see cref="IWorkersOperations"/>, one line per operation.</summary>
internal sealed class WorkersOperations(
    GetWorkerHandler getWorker,
    ListWorkersHandler listWorkers,
    StartWorkerHandler startWorker,
    StopWorkerHandler stopWorker,
    ExtendWorkerLeasesHandler extendWorkerLeases,
    MarkDeadWorkersHandler markDeadWorkers
) : IWorkersOperations
{
    public ValueTask<GetWorkerResponse?> GetAsync(WorkerRef workerRef, CancellationToken ct = default) =>
        getWorker.HandleAsync(new GetWorkerQuery(workerRef), ct);

    public ValueTask<ListWorkersResponse> ListAsync(ListWorkersQuery query, CancellationToken ct = default) =>
        listWorkers.HandleAsync(query, ct);

    public Task<StartWorkerResponse> StartWorkerAsync(StartWorkerCommand command, CancellationToken ct) =>
        startWorker.HandleAsync(command, ct);

    public Task StopWorkerAsync(int namespaceId, int workerId, CancellationToken ct) =>
        stopWorker.HandleAsync(new StopWorkerCommand(namespaceId, workerId), ct);

    public Task<ExtendWorkerLeasesResponse> ExtendWorkerLeasesAsync(
        int workerId,
        int leaseTtlSeconds,
        bool draining,
        IReadOnlyList<(long JobId, int ExecutionNumber)> executions,
        IReadOnlyList<LockToken> locks,
        CancellationToken ct
    ) => extendWorkerLeases.HandleAsync(new ExtendWorkerLeasesCommand(workerId, leaseTtlSeconds, draining, executions, locks), ct);

    public Task<MarkDeadWorkersResponse> MarkDeadWorkersAsync(int deadAfterSeconds, CancellationToken ct) =>
        markDeadWorkers.HandleAsync(new MarkDeadWorkersCommand(deadAfterSeconds), ct);
}
