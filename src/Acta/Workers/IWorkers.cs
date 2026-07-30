namespace Acta;

/// <summary>Workers domain: the keyset-paginated worker list. Reached through <see cref="IJobs.Workers"/>.</summary>
public interface IWorkers
{
    /// <summary>Get one worker by its durable worker-row id, or <see langword="null"/> when it no longer exists.</summary>
    ValueTask<JobWorkerDetail?> GetAsync(int workerId, CancellationToken ct = default);

    /// <summary>List workers most recently seen first, optionally filtered by namespace and status.</summary>
    ValueTask<PagedResult<JobWorkerListItem>> ListAsync(ListWorkersQuery query, CancellationToken ct = default);
}
