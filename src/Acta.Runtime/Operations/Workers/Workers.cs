namespace Acta.Runtime;

/// <summary><see cref="IWorkers"/>, one line per operation.</summary>
internal sealed class Workers(GetWorkerHandler getWorker, ListWorkersHandler listWorkers) : IWorkers
{
    public ValueTask<GetWorkerResponse?> GetAsync(WorkerRef workerRef, CancellationToken ct = default) =>
        getWorker.HandleAsync(new GetWorkerQuery(workerRef), ct);

    public ValueTask<ListWorkersResponse> ListAsync(ListWorkersQuery query, CancellationToken ct = default) =>
        listWorkers.HandleAsync(query, ct);
}
