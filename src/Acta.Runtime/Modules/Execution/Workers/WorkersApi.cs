using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Querying;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary><see cref="IWorkers"/> implementation over the worker store's paged list.</summary>
internal sealed class WorkersApi(IWorkerStore store) : IWorkers
{
    private const string ListOperationName = "ListWorkers";
    private const string OrderWorkers = "last_seen_at_utc desc, id desc";

    public ValueTask<WorkerDetail?> GetAsync(int workerId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerId);
        return store.GetWorkerAsync(workerId, ct);
    }

    public async ValueTask<PagedResult<WorkerListItem>> ListAsync(ListWorkersQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with { JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)) };
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));

        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListWorkersQuery));
        var filterHash = QueryFilterHash.Compute([("ns", query.JobNamespace), ("status", Num(query.Status)), ("tags", tagFilters)]);

        DateTime? cursorLastSeenAtUtc = null;
        int? cursorId = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(
                query.Cursor,
                ListOperationName,
                OrderWorkers,
                filterHash,
                [CursorKeyKind.Utc, CursorKeyKind.Int]
            );
            cursorLastSeenAtUtc = (DateTime)keys[0];
            cursorId = (int)keys[1];
        }

        var page = await store.ListWorkersAsync(
            new WorkerPageRequest(
                query.JobNamespace,
                query.Status,
                cursorLastSeenAtUtc,
                cursorId,
                pageSize + 1,
                query.IncludeTotal,
                tagFilters
            ),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;

        var nextCursor = hasMore
            ? PageCursorCodec.Encode(ListOperationName, OrderWorkers, filterHash, [items[^1].LastHeartbeatAtUtc, items[^1].WorkerId])
            : null;

        return new PagedResult<WorkerListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
