using Acta.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// One <c>events</c> row projected for timeline verification.
/// </summary>
internal sealed record JobEventRecord(
    long Id,
    EventCode EventCode,
    DateTime CreatedAtUtc,
    int? ExecutionNumber,
    JobStatusCode? FromStatus,
    JobStatusCode? ToStatus,
    ExecutionStatusCode? ExecutionStatus,
    int? DurationMs,
    JobEventReasonCode? JobEventReasonCode,
    string? ReasonMessage
);

/// <summary>
/// Test-support read: all <c>events</c> rows for a job id in ascending timeline order
/// (<c>created_at_utc, id</c>). A thin projection over the production <see cref="ILedger.ListEventsAsync"/>
/// read, walked page by page newest first; the rows are reversed here to the ascending order specs
/// assert on. Not a production operation: no live caller reads a job's whole event
/// timeline outside conformance.
/// </summary>
internal static class GetEventsByJobId
{
    public static async Task<IReadOnlyList<JobEventRecord>> Run(IServiceProvider services, long jobId, CancellationToken ct)
    {
        var ledger = services.GetRequiredService<ILedger>();
        var rows = new List<EventListItem>();
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
                    r.JobEventId,
                    r.EventCode,
                    r.CreatedAtUtc,
                    r.ExecutionNumber,
                    r.FromStatus,
                    r.ToStatus,
                    r.ExecutionStatus,
                    r.DurationMs,
                    r.ReasonCode,
                    r.ReasonMessage
                )
            );
        }

        return timeline;
    }
}
