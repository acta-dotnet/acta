using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Querying;

namespace Acta.Runtime.Modules.Operations.Events;

/// <summary>
/// Events feature behavior: validates the public query, owns keyset-cursor decode/encode and page
/// math, and delegates the single-round-trip read to the store port. The IncludeTotal guard is the
/// product rule that a global event count is unbounded work.
/// </summary>
internal sealed class EventsService(IEventStore store)
{
    private const string OrderCreatedDesc = "created_at_utc desc, id desc";
    private const string OperationName = "ListJobEvents";

    public async ValueTask<PagedResult<EventListItem>> ListJobEventsAsync(ListEventsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with { JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)) };
        QueryValidation.ValidateEnum(query.EventCode, nameof(query.EventCode));
        QueryValidation.ValidateEnum(query.ActorCode, nameof(query.ActorCode));
        QueryValidation.ValidateEnum(query.ReasonCode, nameof(query.ReasonCode));
        QueryValidation.ValidatePositiveId(query.JobId, nameof(query.JobId));
        QueryValidation.ValidatePositiveId(query.LineageRootId, nameof(query.LineageRootId));
        if (query.IncludeTotal && query.JobId is null)
        {
            throw new InvalidQueryException("IncludeTotal on ListJobEvents requires JobId; a global event count is unbounded work.");
        }

        QueryValidation.ValidatePositiveId((long?)query.TenantId, nameof(query.TenantId));
        var tenantKey = string.IsNullOrWhiteSpace(query.TenantKey)
            ? null
            : IdentifierSyntax.NormalizeKeyLookup(query.TenantKey, nameof(query.TenantKey));
        QueryValidation.ValidatePositiveId((long?)query.WorkerId, nameof(query.WorkerId));
        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListEventsQuery));

        var filterHash = QueryFilterHash.Compute([
            ("job", Num(query.JobId)),
            ("root", Num(query.LineageRootId)),
            ("ns", query.JobNamespace),
            ("event", Num(query.EventCode)),
            ("def", Num(query.JobDefinitionId)),
            ("tenant", query.TenantId?.ToString(CultureInfo.InvariantCulture)),
            ("tenantKey", tenantKey),
            ("worker", query.WorkerId?.ToString(CultureInfo.InvariantCulture)),
            ("actor", Num(query.ActorCode)),
            ("reason", Num(query.ReasonCode)),
            ("from", query.CreatedFromUtc?.ToString("O", CultureInfo.InvariantCulture)),
            ("to", query.CreatedToUtc?.ToString("O", CultureInfo.InvariantCulture)),
            ("tags", tagFilters),
        ]);

        DateTime? cursorCreatedAtUtc = null;
        long? cursorId = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(
                query.Cursor,
                OperationName,
                OrderCreatedDesc,
                filterHash,
                [CursorKeyKind.Utc, CursorKeyKind.Long]
            );
            cursorCreatedAtUtc = (DateTime)keys[0];
            cursorId = (long)keys[1];
        }

        var page = await store.ListEventsAsync(
            new EventPageRequest(
                query.JobId,
                query.LineageRootId,
                query.JobNamespace,
                query.EventCode,
                query.JobDefinitionId,
                query.TenantId,
                tenantKey,
                query.WorkerId,
                query.ActorCode,
                query.ReasonCode,
                query.CreatedFromUtc,
                query.CreatedToUtc,
                cursorCreatedAtUtc,
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
            ? PageCursorCodec.Encode(OperationName, OrderCreatedDesc, filterHash, [items[^1].CreatedAtUtc, items[^1].JobEventId])
            : null;

        return new PagedResult<EventListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Num(int? value) => value?.ToString(CultureInfo.InvariantCulture);
}
