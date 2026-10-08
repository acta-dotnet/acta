namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="ILedger"/> over the found job: the job list is <see cref="Job"/> alone, the event list is empty, and the
/// overview is a fixed set of counters. The two list reads record the query they were bound to.
/// </summary>
internal sealed class FakeLedger : ILedger
{
    public static readonly ListJobsItem Job = new(
        JobId: 42,
        JobRef: FakeJobs.FoundJobRef,
        JobNamespace: "billing",
        JobName: "send-invoice",
        TenantId: null,
        TenantKey: null,
        ParentJobId: null,
        ParentJobRef: null,
        LineageRootId: null,
        LineageRootJobRef: null,
        DeduplicationKey: "ck-1",
        CorrelationKey: "trace-1",
        Status: JobStatusCode.Ready,
        Priority: JobPriorityCode.Normal,
        Lane: null,
        CreatedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
        ModifiedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
        NextRunAtUtc: null,
        ExecutionNumber: 0,
        FailureCount: 0
    );

    public FakeLedger()
    {
        ListJobs = (query, _) =>
        {
            LastJobsQuery = query;
            return query.Cursor is not null
                ? throw new InvalidPageCursorException("Cursor operation does not match this query.")
                : ValueTask.FromResult(new ListJobsResponse([Job], null, false, 50, null));
        };
        ListEvents = (query, _) =>
        {
            LastEventsQuery = query;
            return ValueTask.FromResult(new ListEventsResponse([], null, false, 50, query.IncludeTotal ? 0L : null));
        };
        GetOverview = static (query, _) => ValueTask.FromResult(Overview(query));
    }

    public ListJobsQuery? LastJobsQuery { get; private set; }

    public ListEventsQuery? LastEventsQuery { get; private set; }

    public Func<ListJobsQuery, CancellationToken, ValueTask<ListJobsResponse>> ListJobs { get; set; }

    public Func<ListEventsQuery, CancellationToken, ValueTask<ListEventsResponse>> ListEvents { get; set; }

    public Func<GetOverviewQuery, CancellationToken, ValueTask<OverviewSnapshot>> GetOverview { get; set; }

    ValueTask<ListJobsResponse> ILedger.ListJobsAsync(ListJobsQuery query, CancellationToken ct) => ListJobs(query, ct);

    ValueTask<ListEventsResponse> ILedger.ListEventsAsync(ListEventsQuery query, CancellationToken ct) => ListEvents(query, ct);

    ValueTask<OverviewSnapshot> ILedger.GetOverviewAsync(GetOverviewQuery query, CancellationToken ct) => GetOverview(query, ct);

    private static OverviewSnapshot Overview(GetOverviewQuery query) =>
        query.JobNamespace is not null && query.JobNamespace.Any(ch => !char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-')
            ? throw new InvalidQueryException("JobNamespace must be a kebab-case identifier.")
            : new OverviewSnapshot(3, 120, 1, 2, 4, 1, 1, 2, 5, 10, 3, 8, 90);
}
