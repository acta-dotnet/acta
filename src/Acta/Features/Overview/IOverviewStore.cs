namespace Acta.Features.Overview;

/// <summary>
/// Persistence port for the cross-feature Overview read model: the one-row health summary the
/// dashboard's landing page renders. Implementations own command creation, parameter binding, row
/// mapping, and the single-round-trip guarantee; the query arrives validated.
/// </summary>
internal interface IOverviewStore
{
    /// <summary>
    /// One-row health snapshot - job status counts, oldest due Ready age, unresolved alert counts,
    /// dead/stale worker counts, and the due-soon schedule count - optionally namespace-scoped. All
    /// times come from the database clock; an unknown namespace yields all-zero counters.
    /// </summary>
    ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct);
}
