using System.Text;
using Acta.Tests.AspNetCore.Fakes;

namespace Acta.Tests.Contracts;

/// <summary>
/// The fake world the contract's reads are served from: every list holds one item, and the optional members of the job,
/// its steps, checkpoints, explanation, lineage, and the listed events, alerts, schedules, and workers carry values. The
/// served-body fact checks each schema against it, and the document's examples are its answers.
/// </summary>
internal static class StagedFakes
{
    private static readonly DateTime At = new(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc);

    public static FakeOperations Create()
    {
        var fakes = new FakeOperations();
        var jobs = fakes.Jobs;
        jobs.StoredInput = Json("""{"invoiceId":42}""");
        jobs.StoredResult = Json("""{"sent":true}""");
        jobs.StoredSteps = [new ListJobStepsItem("charge-card", StepStatusCode.Pending, 2, At.AddMinutes(5), "Card declined.", At, At)];
        jobs.StoredCheckpoints =
        [
            new ListJobCheckpointsItem(
                CheckpointKindCode.Signal,
                "approval",
                CheckpointStatusCode.Pending,
                At.AddHours(1),
                JobPayload.Bytes([1, 2, 3]),
                At,
                At
            ),
        ];
        var get = jobs.Get;
        jobs.Get = async (job, ct) =>
        {
            var found = await get(job, ct);
            return found?.JobId == 42 ? Job : found;
        };
        jobs.Explain = (job, _) => ValueTask.FromResult<ExplainJobResponse?>(IsFound(job) ? Explanation : null);
        jobs.GetLineageMap = (job, _, _) => ValueTask.FromResult<GetJobLineageMapResponse?>(IsFound(job) ? Lineage : null);

        fakes.Ledger.ListEvents = (query, _) =>
            ValueTask.FromResult(new ListEventsResponse([Event], null, false, 50, query.IncludeTotal ? 1L : null));
        fakes.Alerts.List = (query, _) =>
            ValueTask.FromResult(new ListAlertsResponse([Alert], null, false, 50, query.IncludeTotal ? 1L : null));
        fakes.Alerts.Get = (alertRef, _) => ValueTask.FromResult(alertRef == FakeAlerts.KnownAlertRef ? AlertDetail : null);
        fakes.Workers.List = (query, _) =>
            ValueTask.FromResult(new ListWorkersResponse([Worker], null, false, 50, query.IncludeTotal ? 1L : null));
        fakes.Schedules.List = (query, _) =>
            ValueTask.FromResult(new ListSchedulesResponse([Schedule], null, false, 50, query.IncludeTotal ? 1L : null));

        var listJobs = fakes.Ledger.ListJobs;
        fakes.Ledger.ListJobs = async (query, ct) => await listJobs(query, ct) with { Items = [JobItem] };
        var definitions = fakes.Definitions.List;
        fakes.Definitions.List = (query, ct) => definitions(query with { JobNamespace = "billing" }, ct);

        // A fake's own list of several items keeps one: the billing namespace, the published calendar version, and the
        // first of each other list.
        var namespaces = fakes.Namespaces.List;
        fakes.Namespaces.List = async (query, ct) =>
        {
            var page = await namespaces(query, ct);
            return page with { Items = Last(page.Items) };
        };
        var versions = fakes.Calendars.ListVersions;
        fakes.Calendars.ListVersions = async (query, ct) =>
        {
            var page = await versions(query, ct);
            return page with { Items = Last(page.Items) };
        };
        var calendars = fakes.Calendars.List;
        fakes.Calendars.List = async (query, ct) =>
        {
            var page = await calendars(query, ct);
            return page with { Items = First(page.Items) };
        };
        var tags = fakes.Tags.List;
        fakes.Tags.List = async (query, ct) =>
        {
            var page = await tags(query, ct);
            return page with { Items = First(page.Items) };
        };
        return fakes;
    }

    private static bool IsFound(JobLookup job) => job.JobRef == FakeJobs.FoundJobRef || job.JobId == 42;

    private static IReadOnlyList<T> First<T>(IReadOnlyList<T> items) => items.Count > 1 ? [items[0]] : items;

    private static IReadOnlyList<T> Last<T>(IReadOnlyList<T> items) => items.Count > 1 ? [items[^1]] : items;

    /// <summary>The found job's parent and lineage root.</summary>
    private static readonly JobRef ParentJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000040"));

    private static JobPayload Json(string text) => JobPayload.FromBytes(JobPayloadFormat.Json, Encoding.UTF8.GetBytes(text));

    private static readonly GetJobResponse Job = new(
        JobId: 42,
        JobRef: FakeJobs.FoundJobRef,
        Kind: JobKindCode.Ordinary,
        JobNamespace: "billing",
        DefinitionId: 5,
        JobName: "send-invoice",
        LineageRootJobId: 40,
        LineageRootJobRef: ParentJobRef,
        ParentJobId: 40,
        ParentJobRef: ParentJobRef,
        TenantId: 1,
        TenantName: "cust-001",
        DeduplicationKey: "ck-1",
        CorrelationKey: "order-1042",
        ConcurrencyKey: "customer:1042",
        InputFormatId: JobPayloadFormat.Json.Id,
        Status: JobStatusCode.Ready,
        Priority: JobPriorityCode.High,
        Lane: "invoices",
        BlockedBehindJobId: 7,
        BlockedBehindJobRef: FakeJobs.ChildJobRef,
        NextRunAtUtc: At.AddMinutes(5),
        ExecutionNumber: 2,
        FailureCount: 1,
        LeasedByWorkerId: 42,
        LeaseExpiresAtUtc: At.AddMinutes(1),
        RetentionUntilUtc: At.AddDays(90),
        CreatedAtUtc: At,
        ModifiedAtUtc: At,
        LeasedByWorkerRef: FakeWorkers.KnownWorkerRef,
        Version: FakeJobs.CurrentJobVersion
    );

    private static readonly ListJobsItem JobItem = new(
        JobId: 42,
        JobRef: FakeJobs.FoundJobRef,
        JobNamespace: "billing",
        JobName: "send-invoice",
        TenantId: 1,
        TenantName: "cust-001",
        ParentJobId: 40,
        ParentJobRef: ParentJobRef,
        LineageRootJobId: 40,
        LineageRootJobRef: ParentJobRef,
        DeduplicationKey: "ck-1",
        CorrelationKey: "order-1042",
        Status: JobStatusCode.Ready,
        Priority: JobPriorityCode.High,
        Lane: "invoices",
        CreatedAtUtc: At,
        ModifiedAtUtc: At,
        NextRunAtUtc: At.AddMinutes(5),
        ExecutionNumber: 2,
        FailureCount: 1
    );

    private static readonly ExplainJobResponse Explanation = new(
        JobId: 42,
        JobRef: FakeJobs.FoundJobRef,
        JobNamespace: "billing",
        JobName: "send-invoice",
        Status: JobStatusCode.Ready,
        StatusMeaning: JobStatusCode.Ready.Description,
        Headline: "Ready and eligible for claim, waiting for a worker to pick it up.",
        ActiveWait: new ExplainJobWait(CheckpointKindCode.Signal, "approval", At.AddHours(1)),
        Lease: new ExplainJobLease(
            42,
            "worker-host",
            At.AddMinutes(1),
            false,
            At,
            false,
            "Reclaimed if the lease lapses.",
            FakeWorkers.KnownWorkerRef
        ),
        LastExecutedBy: "worker-host",
        Steps: [new ExplainJobStep("charge-card", StepStatusCode.Pending, "Retries after its backoff.")],
        StepsHasMore: false,
        Reason: "The card was declined.",
        NextActions: [new ExplainJobAction("none", "no action needed - a worker will claim it")]
    );

    private static readonly GetJobLineageMapResponse Lineage = new(
        Ancestors:
        [
            new GetJobLineageMapNode(40, ParentJobRef, "billing", "close-month", JobStatusCode.Suspended, null, null, null, null, At, At),
        ],
        AncestorsHasMore: false,
        Job: new GetJobLineageMapNode(
            42,
            FakeJobs.FoundJobRef,
            "billing",
            "send-invoice",
            JobStatusCode.Ready,
            40,
            ParentJobRef,
            40,
            ParentJobRef,
            At,
            At
        ),
        Steps: [new GetJobLineageMapStep("charge-card", StepStatusCode.Pending, "Retries after its backoff.")],
        StepsHasMore: false,
        ActiveWait: new GetJobLineageMapWait(CheckpointKindCode.Signal, "approval", At.AddHours(1)),
        Children: [new GetJobLineageMapChild(7, FakeJobs.ChildJobRef, "render-pdf", JobStatusCode.Executing, At, At)],
        ChildrenHasMore: false
    );

    private static readonly ListEventsItem Event = new(
        EventId: 9001,
        EventCode: EventCode.JobExecutionFailed,
        CreatedAtUtc: At,
        JobNamespace: "billing",
        EntityKind: EntityCode.Job,
        EntityId: 42,
        JobName: "send-invoice",
        JobRef: FakeJobs.FoundJobRef,
        LineageRootJobId: 40,
        LineageRootJobRef: ParentJobRef,
        DefinitionId: 5,
        TenantId: 1,
        TenantName: "cust-001",
        WorkerId: 42,
        WorkerRef: FakeWorkers.KnownWorkerRef,
        ExecutionNumber: 2,
        DurationMs: 1520,
        Message: "Invoice provider timed out.",
        DetailsText: """{"reason":"timeout"}"""
    );

    private static readonly ListAlertsItem Alert = new(
        AlertRef: FakeAlerts.KnownAlertRef,
        AlertId: 9001,
        JobNamespace: "billing",
        JobId: 42,
        JobRef: FakeJobs.FoundJobRef,
        Origin: AlertOriginCode.Automatic,
        Severity: AlertSeverityCode.Critical,
        Kind: AlertKindCode.FinalFailure,
        Title: "Invoice provider timed out",
        Message: "The provider returned 504 three times.",
        ChannelName: "default",
        OccurrenceCount: 3,
        ResolvedAtUtc: At.AddHours(2),
        DeliveryStatus: AlertDeliveryStatusCode.RetryAfter,
        RetryCount: 1,
        RetryAfterUtc: At.AddMinutes(10),
        CreatedAtUtc: At,
        ModifiedAtUtc: At,
        AcknowledgedAtUtc: At.AddHours(1)
    );

    private static readonly GetAlertResponse? AlertDetail = new(
        Alert.AlertRef,
        Alert.AlertId,
        Alert.JobNamespace,
        Alert.JobId,
        Alert.JobRef,
        Alert.Origin,
        Alert.Severity,
        Alert.Kind,
        Alert.Title,
        Alert.Message,
        Alert.ChannelName,
        Alert.OccurrenceCount,
        Alert.ResolvedAtUtc,
        Alert.DeliveryStatus,
        Alert.RetryCount,
        Alert.RetryAfterUtc,
        Alert.CreatedAtUtc,
        Alert.ModifiedAtUtc,
        Alert.AcknowledgedAtUtc
    );

    private static readonly ListWorkersItem Worker = new(
        FakeWorkers.KnownWorkerRef,
        42,
        "billing",
        WorkerStatusCode.Active,
        "worker-host",
        "deploy-42",
        "engine-1",
        ".NET 10.0.0",
        4242,
        8,
        At,
        At,
        At
    );

    private static readonly ListSchedulesItem Schedule = new(
        ScheduleId: 3,
        JobId: 42,
        DefinitionId: 5,
        JobNamespace: "billing",
        JobName: "send-invoice",
        ScheduleName: "nightly",
        Origin: ScheduleOriginCode.Definition,
        ExpressionKind: ScheduleExpressionKindCode.Cron,
        Expression: "0 2 * * *",
        TimeZoneId: "Europe/Ljubljana",
        MisfireStrategy: MisfireStrategyCode.CatchUpOnce,
        NextOccurrenceAtUtc: At.AddHours(20),
        NextEvaluationAtUtc: At.AddHours(20),
        EvaluationStatus: EvaluationStatusCode.Scheduled,
        Status: ScheduleStatusCode.Active,
        PausedUntilUtc: At.AddHours(1),
        CreatedAtUtc: At,
        ModifiedAtUtc: At,
        Version: 2,
        JobRef: FakeJobs.FoundJobRef,
        MaxOutstanding: 1,
        OutstandingCount: 0,
        FullBehavior: ScheduleFullBehaviorCode.Wait,
        ScheduledDelivery: ScheduledDeliveryCode.Permanent,
        ExpireAfterSeconds: 3600,
        ExpireAction: ScheduleExpireActionCode.Discard,
        AvailabilityCalendarName: "office-hours"
    );
}
