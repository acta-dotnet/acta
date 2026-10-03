namespace Acta;

/// <summary>
/// Cross-resource reads over the work ledger: the job list, its audit trail, and the aggregate
/// health snapshot. Reached through <see cref="IActaOperations.Ledger"/>. Single-job reads stay on
/// <see cref="IJobs"/> (lookup-addressed); per-resource lists stay on their own domains.
/// </summary>
public interface ILedger
{
    /// <summary>List jobs newest first, optionally filtered by namespace, status, definition, tenant, correlation id, or tags.</summary>
    ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default);

    /// <summary>List audit events newest first, optionally scoped to a job, lineage, namespace, or event code.</summary>
    ValueTask<PagedResult<EventListItem>> ListEventsAsync(ListEventsQuery query, CancellationToken ct = default);

    /// <summary>One-shot dashboard health counters, optionally scoped to a namespace.</summary>
    ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default);
}
