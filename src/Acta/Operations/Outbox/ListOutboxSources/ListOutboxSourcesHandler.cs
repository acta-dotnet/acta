using System.Text.Json;

namespace Acta;

/// <summary>
/// <see cref="IOutbox.ListSourcesAsync"/>: one item per namespace whose slot exists, composed cross-peer from
/// the slot's <see cref="OutboxTickSummary.VariableName"/> variable, so it never opens a source database. Paging
/// follows the namespace catalog. The registry is present only in processes that declared a relay.
/// </summary>
internal sealed class ListOutboxSourcesHandler(
    GetJobIdBySlotHandler getJobIdBySlot,
    GetJobCheckpointValueHandler getJobCheckpointValue,
    IJobs jobs,
    INamespaces namespaces,
    OutboxRelayRegistry? registry = null
)
{
    public async Task<ListOutboxSourcesResponse> HandleAsync(ListOutboxSourcesQuery query, CancellationToken ct)
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
            var page = await namespaces.ListAsync(new ListNamespacesQuery(PageSize: pageSize, Cursor: query.Cursor), ct);
            (names, nextCursor, hasMore) = ([.. page.Items.Select(item => item.JobNamespace)], page.NextCursor, page.HasMore);
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

    private async ValueTask<ListOutboxSourcesItem?> ComposeAsync(string jobNamespace, CancellationToken ct)
    {
        if (await OutboxSlot.FindAsync(getJobIdBySlot, jobs, jobNamespace, ct) is not { } slot)
        {
            return null;
        }

        // One read answers the tick summary and the time of the write that stored it, so both come from the same pass.
        var written = await getJobCheckpointValue.ReadAsync(
            JobLookup.ById(slot.JobId),
            CheckpointKindCode.Variable,
            OutboxTickSummary.VariableName,
            ct
        );
        var status = written is null
            ? null
            : JsonSerializer.Deserialize(written.Value, OutboxTickSummaryJsonContext.Default.OutboxTickSummary);

        // Only a successful pass writes the variable, so its last write is that pass's end.
        return new ListOutboxSourcesItem(
            jobNamespace,
            slot.JobRef,
            status is null ? null : written!.ModifiedAtUtc,
            status?.Backlog,
            status?.QuarantineTotal,
            registry?.HasRelay(jobNamespace) == true
        );
    }
}
