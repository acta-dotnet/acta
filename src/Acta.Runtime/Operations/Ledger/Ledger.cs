namespace Acta.Runtime;

/// <summary><see cref="ILedger"/>, one line per operation.</summary>
internal sealed class Ledger(ListJobsHandler listJobs, ListEventsHandler listEvents, GetOverviewHandler getOverview) : ILedger
{
    public ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default) =>
        listJobs.HandleAsync(query, ct);

    public ValueTask<PagedResult<EventListItem>> ListEventsAsync(ListEventsQuery query, CancellationToken ct = default) =>
        listEvents.HandleAsync(query, ct);

    public ValueTask<OverviewSnapshot> GetOverviewAsync(GetOverviewQuery query, CancellationToken ct = default) =>
        getOverview.HandleAsync(query, ct);
}
