using System.Text;

namespace Acta;

/// <summary>
/// <see cref="IOutbox.ListSourcesAsync"/>: one item per namespace whose slot exists, composed cross-peer from
/// the slot's persisted tick summary, so it never opens a source database. Paging follows the namespace
/// catalog. The registry is present only in processes that declared a relay.
/// </summary>
internal sealed class ListOutboxSourcesHandler(IJobs jobs, INamespaces namespaces, OutboxRelayRegistry? registry = null)
{
    public async ValueTask<ListOutboxSourcesResponse> HandleAsync(ListOutboxSourcesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));

        IReadOnlyList<string> names;
        string? nextCursor = null;
        var hasMore = false;
        if (jobNamespace is not null)
        {
            names = [jobNamespace];
        }
        else
        {
            var page = await namespaces.ListNamesAsync(new ListNamespaceNamesQuery(PageSize: pageSize, Cursor: query.Cursor), ct);
            (names, nextCursor, hasMore) = (page.Items, page.NextCursor, page.HasMore);
        }

        var items = new List<ListOutboxSourcesItem>();
        foreach (var name in names)
        {
            if (await ComposeAsync(name, ct) is { } item)
            {
                items.Add(item);
            }
        }

        return new ListOutboxSourcesResponse(items, nextCursor, hasMore, pageSize, null);
    }

    /// <summary>
    /// Reads the summary's last "&lt;token&gt;N" value up to the next space, so trailing tokens don't spoil the
    /// parse. Null means the token is absent (an older summary format): unknown, not zero.
    /// </summary>
    internal static long? ParseToken(string? tick, string token)
    {
        var index = tick?.LastIndexOf(token, StringComparison.Ordinal) ?? -1;
        if (index < 0)
        {
            return null;
        }

        var span = tick!.AsSpan(index + token.Length);
        var end = span.IndexOf(' ');
        return long.TryParse(end >= 0 ? span[..end] : span, out var value) ? value : null;
    }

    private async ValueTask<ListOutboxSourcesItem?> ComposeAsync(string jobNamespace, CancellationToken ct)
    {
        if (await OutboxSlot.FindAsync(jobs, jobNamespace, ct) is not { } slot)
        {
            return null;
        }

        var result = (await jobs.GetResultAsync(JobLookup.ById(slot.JobId), ct))?.Result;
        var tick =
            result is { } payload && payload.Format.Id == JobPayloadFormat.Text.Id ? Encoding.UTF8.GetString(payload.Data.Span) : null;

        return new ListOutboxSourcesItem(
            jobNamespace,
            slot.JobRef,
            tick,
            ParseToken(tick, "backlog="),
            ParseToken(tick, "quarantine="),
            registry?.HasRelay(jobNamespace) == true
        );
    }
}
