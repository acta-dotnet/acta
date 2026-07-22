using Acta.Features.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// One <c>events</c> row projected for timeline verification.
/// </summary>
internal sealed record JobEventRecord(
    long Id,
    JobEventCode JobEventCode,
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
/// (<c>created_at_utc, id</c>). A thin projection over the production <see cref="IEventStore"/>
/// read, which returns the newest-first keyset page; the rows are reversed here to the ascending
/// order specs assert on. Not a production operation: no live caller reads a job's whole event
/// timeline outside conformance.
/// </summary>
internal static class GetEventsByJobId
{
    public static async Task<IReadOnlyList<JobEventRecord>> Run(IServiceProvider services, long jobId, CancellationToken ct)
    {
        var page = await services
            .GetRequiredService<IEventStore>()
            .ListEventsAsync(
                new EventPageRequest(
                    jobId,
                    LineageRootId: null,
                    JobNamespace: null,
                    EventCode: null,
                    JobDefinitionId: null,
                    TenantId: null,
                    WorkerId: null,
                    ActorCode: null,
                    ReasonCode: null,
                    CreatedFromUtc: null,
                    CreatedToUtc: null,
                    CursorCreatedAtUtc: null,
                    CursorId: null,
                    Take: 10_000,
                    IncludeTotal: false
                ),
                ct
            );

        var rows = page.Rows;
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
