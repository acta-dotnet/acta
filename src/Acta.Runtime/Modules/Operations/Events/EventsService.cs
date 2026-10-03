using System.Globalization;

namespace Acta.Runtime;

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

        var paging = Paging.Start(
            OperationName,
            OrderCreatedDesc,
            [CursorKeyKind.Utc, CursorKeyKind.Long],
            query.Cursor,
            query.PageSize,
            [
                ("job", Num(query.JobId)),
                ("root", Num(query.LineageRootId)),
                ("ns", query.JobNamespace),
                ("event", Num(query.EventCode)),
                ("def", Num(query.DefinitionId)),
                ("tenant", query.TenantId?.ToString(CultureInfo.InvariantCulture)),
                ("tenantKey", tenantKey),
                ("worker", query.WorkerId?.ToString(CultureInfo.InvariantCulture)),
                ("actor", Num(query.ActorCode)),
                ("reason", Num(query.ReasonCode)),
                ("from", query.CreatedFromUtc?.ToString("O", CultureInfo.InvariantCulture)),
                ("to", query.CreatedToUtc?.ToString("O", CultureInfo.InvariantCulture)),
                ("tags", tagFilters),
            ]
        );

        var page = await store.ListEventsAsync(
            new EventPageRequest(
                query.JobId,
                query.LineageRootId,
                query.JobNamespace,
                query.EventCode,
                query.DefinitionId,
                query.TenantId,
                tenantKey,
                query.WorkerId,
                query.ActorCode,
                query.ReasonCode,
                query.CreatedFromUtc,
                query.CreatedToUtc,
                (DateTime?)paging.After?[0],
                (long?)paging.After?[1],
                paging.Fetch,
                query.IncludeTotal,
                tagFilters
            ),
            ct
        );
        return paging.Page(page.Rows, page.Total, static item => [item.CreatedAtUtc, item.JobEventId]);
    }

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Num(int? value) => value?.ToString(CultureInfo.InvariantCulture);
}
