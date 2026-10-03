namespace Acta;

/// <summary>Workers domain: the keyset-paginated worker list. Reached through <see cref="IActaOperations.Workers"/>.</summary>
public interface IWorkers
{
    /// <summary>Get one worker by its public ref, or <see langword="null"/> when it no longer exists.</summary>
    ValueTask<WorkerDetail?> GetAsync(WorkerRef workerRef, CancellationToken ct = default);

    /// <summary>List workers most recently seen first, optionally filtered by namespace and status.</summary>
    ValueTask<PagedResult<WorkerListItem>> ListAsync(ListWorkersQuery query, CancellationToken ct = default);
}
