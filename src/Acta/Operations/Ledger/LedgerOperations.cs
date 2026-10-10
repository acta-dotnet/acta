namespace Acta;

/// <summary><see cref="ILedgerOperations"/>, one line per operation.</summary>
internal sealed class LedgerOperations(ListJobsHandler listJobs, ListEventsHandler listEvents, GetOverviewHandler getOverview)
    : ILedgerOperations
{
    public ValueTask<ListJobsResponse> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default) =>
        listJobs.HandleAsync(query, ct);

    public ValueTask<ListEventsResponse> ListEventsAsync(ListEventsQuery query, CancellationToken ct = default) =>
        listEvents.HandleAsync(query, ct);

    public ValueTask<GetOverviewResponse> GetOverviewAsync(GetOverviewQuery query, CancellationToken ct = default) =>
        getOverview.HandleAsync(query, ct);
}
