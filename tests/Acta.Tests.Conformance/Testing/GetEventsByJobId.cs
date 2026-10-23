using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// One job-subject <c>events</c> row projected for timeline verification. The details facts (<see cref="Reason"/>,
/// <see cref="To"/>, <see cref="Outcome"/>, <see cref="Actor"/>, <see cref="Schedule"/>) read the JSON details text.
/// </summary>
internal sealed record JobEventRecord(
    long Id,
    EventCode EventCode,
    DateTime CreatedAtUtc,
    int? ExecutionNumber,
    int? DurationMs,
    int? WorkerId,
    string? Message,
    string? DetailsText
)
{
    public string? Reason => Detail("reason");

    public string? To => Detail("to");

    public string? Outcome => Detail("outcome");

    public string? Actor => Detail("actor");

    public string? Schedule => Detail("schedule");

    /// <summary>The named text fact of the JSON details, or null when absent.</summary>
    public string? Detail(string name)
    {
        if (DetailsText is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(DetailsText);
        return
            document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

/// <summary>
/// Test-support read: all job-subject <c>events</c> rows for a job id in ascending timeline order
/// (<c>created_at_utc, event_id</c>). A thin projection over the production <see cref="ILedger.ListEventsAsync"/>
/// read, walked page by page newest first; the rows are reversed here to the ascending order specs
/// assert on. Not a production operation: no live caller reads a job's whole event
/// timeline outside conformance.
/// </summary>
internal static class GetEventsByJobId
{
    public static async Task<IReadOnlyList<JobEventRecord>> Run(IServiceProvider services, long jobId, CancellationToken ct)
    {
        var ledger = services.GetRequiredService<ILedger>();
        var rows = new List<ListEventsItem>();
        string? cursor = null;
        do
        {
            var page = await ledger.ListEventsAsync(new ListEventsQuery(JobId: jobId, PageSize: 100, Cursor: cursor), ct);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        var timeline = new List<JobEventRecord>(rows.Count);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var r = rows[i];
            timeline.Add(
                new JobEventRecord(
                    r.EventId,
                    r.EventCode,
                    r.CreatedAtUtc,
                    r.ExecutionNumber,
                    r.DurationMs,
                    r.WorkerId,
                    r.Message,
                    r.DetailsText
                )
            );
        }

        return timeline;
    }
}
