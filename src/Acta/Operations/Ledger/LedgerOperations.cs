namespace Acta;

/// <summary><see cref="ILedger"/>, one line per operation.</summary>
internal sealed class LedgerOperations(ListJobsHandler listJobs, ListEventsHandler listEvents, GetOverviewHandler getOverview) : ILedger
{
    public Task<ListJobsResponse> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default) => listJobs.HandleAsync(query, ct);

    public Task<ListEventsResponse> ListEventsAsync(ListEventsQuery query, CancellationToken ct = default) =>
        listEvents.HandleAsync(query, ct);

    public Task<GetOverviewResponse> GetOverviewAsync(GetOverviewQuery query, CancellationToken ct = default) =>
        getOverview.HandleAsync(query, ct);
}
