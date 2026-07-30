using Acta.Querying;

namespace Acta.Modules.Operations.Overview;

/// <summary>
/// Overview feature behavior: validates the public query and delegates to the store port. Exists so
/// the public facade stays contract-shaped and every provider store receives the same validated
/// input.
/// </summary>
internal sealed class OverviewService(IOverviewStore store)
{
    public ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query with { JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)) };
        ArgumentOutOfRangeException.ThrowIfLessThan(query.StaleWorkerAfterSeconds, 1, nameof(query.StaleWorkerAfterSeconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(query.DueSoonWindowSeconds, 1, nameof(query.DueSoonWindowSeconds));

        return store.GetOverviewAsync(query, ct);
    }
}
