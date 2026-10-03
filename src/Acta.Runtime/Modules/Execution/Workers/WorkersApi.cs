using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="IWorkers"/> implementation over the worker store's paged list.</summary>
internal sealed class WorkersApi(IWorkerStore store) : IWorkers
{
    private const string ListOperationName = "ListWorkers";
    private const string OrderWorkers = "last_seen_at_utc desc, id desc";

    public ValueTask<WorkerDetail?> GetAsync(WorkerRef workerRef, CancellationToken ct = default) =>
        store.GetWorkerAsync(workerRef.Value, ct);

    public async ValueTask<PagedResult<WorkerListItem>> ListAsync(ListWorkersQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query with { JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)) };
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));

        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListWorkersQuery));
        var paging = Paging.Start(
            ListOperationName,
            OrderWorkers,
            [CursorKeyKind.Utc, CursorKeyKind.Int],
            query.Cursor,
            query.PageSize,
            [("ns", query.JobNamespace), ("status", Num(query.Status)), ("tags", tagFilters)]
        );

        var page = await store.ListWorkersAsync(
            new WorkerPageRequest(
                query.JobNamespace,
                query.Status,
                (DateTime?)paging.After?[0],
                (int?)paging.After?[1],
                paging.Fetch,
                query.IncludeTotal,
                tagFilters
            ),
            ct
        );
        return paging.Page(page.Rows, page.Total, static item => [item.LastHeartbeatAtUtc, item.WorkerId]);
    }

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
