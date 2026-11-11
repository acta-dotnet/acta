using System.Text;
using Acta.Tests.AspNetCore.Fakes;

namespace Acta.Tests.Contracts;

/// <summary>
/// The fake world the contract's reads are served from, one situation throughout: job 42 is ready again at 06:05, when its
/// retry is due after its second attempt failed in its pending step at 06:00. Every list holds one item but the calendar's two
/// versions, and each optional member carries a value wherever that situation has one. The served-body fact checks each
/// schema against it, and the document's examples are its answers.
/// </summary>
internal static class StagedFakes
{
    private static readonly DateTime At = new(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>The permanent slot the definition's schedule fires; job 42 is an ordinary job of the same definition.</summary>
    private static readonly JobRef SlotJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000045"));

    /// <summary>
    /// The schedule's next occurrence: 10:00 UTC today, a Friday, inside the office-hours calendar it names for availability.
    /// </summary>
    private static readonly DateTime NextRun = At.AddHours(4);

    public static FakeOperations Create()
    {
        var fakes = new FakeOperations();
        var jobs = fakes.Jobs;
        jobs.StoredInput = Json("""{"invoiceId":42}""");
        jobs.StoredResult = Json("""{"sent":true}""");
        jobs.StoredSteps = [new ListJobStepsItem("post-invoice", StepStatusCode.Pending, null, At, At)];
        // A signal the job awaited before and an operator raised, so its payload is stored and no wait is pending.
        jobs.StoredCheckpoints =
        [
            new ListJobCheckpointsItem(
                CheckpointKindCode.Signal,
                "approval",
                CheckpointStatusCode.Set,
                null,
                JobPayload.Bytes([1, 2, 3]),
                At.AddMinutes(-30),
                At.AddMinutes(-20)
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
        fakes.Schedules.Get = (schedule, _) => ValueTask.FromResult(schedule.ScheduleName == Schedule.ScheduleName ? ScheduleDetail : null);
        fakes.Schedules.Preview = (schedule, _, _) =>
            ValueTask.FromResult<PreviewScheduleResponse?>(
                schedule.ScheduleName == Schedule.ScheduleName
                    ? new PreviewScheduleResponse(Schedule.Expression, Schedule.TimeZoneId!, [NextRun, NextRun.AddDays(3)])
                    : null
            );

        var listJobs = fakes.Ledger.ListJobs;
        fakes.Ledger.ListJobs = async (query, ct) => await listJobs(query, ct) with { Items = [JobItem] };
        var definitions = fakes.Definitions.List;
        fakes.Definitions.List = (query, ct) => definitions(query with { JobNamespace = "billing" }, ct);

        // A fake's own list of several items keeps one: the billing namespace, and the first of each other list. The
        // calendar's versions page keeps both versions, so it agrees with the family's latest version and its total.
        var namespaces = fakes.Namespaces.List;
        fakes.Namespaces.List = async (query, ct) =>
        {
            var page = await namespaces(query, ct);
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

        // The errors the document's examples provoke beyond the fakes' own, each as production answers it: the overview
        // validates its namespace filter; the rejected job ref names a job with no input; "rejected" is a suspended
        // namespace to the enqueue, one with no outbox relay on this host to the quarantine listing, and a schedule naming
        // an unpublished calendar to the preview; and a tag write on a target the fakes know as missing finds nothing.
        var overview = fakes.Ledger.GetOverview;
        fakes.Ledger.GetOverview = (query, ct) =>
        {
            _ = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
            return overview(query, ct);
        };
        var getId = jobs.GetId;
        jobs.GetId = (job, ct) =>
            job.JobRef == FakeJobs.RejectedJobRef ? ValueTask.FromResult<GetJobIdResponse?>(new(RejectedJobId)) : getId(job, ct);
        var getInput = jobs.GetInput;
        jobs.GetInput = (job, ct) => job.JobId == RejectedJobId ? ValueTask.FromResult<GetJobInputResponse?>(null) : getInput(job, ct);
        var enqueue = jobs.Enqueue;
        jobs.Enqueue = (request, ct) =>
            request.JobNamespace == "rejected"
                ? throw new EnqueueRejectedException(
                    EnqueueRejectionReason.NamespaceSuspended,
                    "Enqueue rejected: one or more rows reference a suspended namespace."
                )
                : enqueue(request, ct);
        var quarantined = fakes.Outbox.ListQuarantined;
        fakes.Outbox.ListQuarantined = (query, ct) =>
            query.JobNamespace == "rejected"
                ? throw new InvalidOperationException(
                    "Namespace 'rejected' has no outbox relay registered in this process. Quarantined rows live in the producer's "
                        + "source database, which only a host that called AddOutboxRelay for this namespace can read; run the listing "
                        + "there (sources report IsLocal), or requeue/discard from any peer."
                )
                : quarantined(query, ct);
        var preview = fakes.Schedules.Preview;
        fakes.Schedules.Preview = (schedule, count, ct) =>
            schedule.ScheduleName == "rejected"
                ? throw new CalendarUnavailableException(
                    "The availability calendar 'office-hours' has no published version in the namespace's calendar catalog or the "
                        + "shared one."
                )
                : preview(schedule, count, ct);
        var upsert = fakes.Tags.Upsert;
        fakes.Tags.Upsert = (target, tag, ct) =>
            IsMissing(target) ? ValueTask.FromResult(new UpsertTagResponse(ControlAction.NotFound)) : upsert(target, tag, ct);
        var remove = fakes.Tags.Remove;
        fakes.Tags.Remove = (target, name, ct) =>
            IsMissing(target) ? ValueTask.FromResult(new RemoveTagResponse(ControlAction.NotFound)) : remove(target, name, ct);
        return fakes;
    }

    /// <summary>The job the rejected ref names: a job with no input, so an input update has nothing to replace.</summary>
    private const long RejectedJobId = 43;

    private static bool IsMissing(TagTarget target) =>
        target.Lookup is "missing" or (_, "missing") || target.Lookup is JobLookup { JobRef: var job } && job == FakeJobs.MissingJobRef;

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
        BlockedBehindJobId: null,
        BlockedBehindJobRef: null,
        NextRunAtUtc: At.AddMinutes(5),
        ExecutionNumber: 2,
        FailureCount: 1,
        LeasedByWorkerId: null,
        LeaseExpiresAtUtc: null,
        RetentionUntilUtc: null,
        CreatedAtUtc: At.AddHours(-1),
        ModifiedAtUtc: At,
        LeasedByWorkerRef: null,
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
        CreatedAtUtc: At.AddHours(-1),
        ModifiedAtUtc: At,
        NextRunAtUtc: At.AddMinutes(5),
        ExecutionNumber: 2,
        FailureCount: 1
    );

    // As ExplainJobHandler words a ready job whose next run is five minutes away, with no wait and no lease; the last
    // worker is named by its ref, with its deployment version beside it.
    private static readonly ExplainJobResponse Explanation = new(
        JobId: 42,
        JobRef: FakeJobs.FoundJobRef,
        JobNamespace: "billing",
        JobName: "send-invoice",
        Status: JobStatusCode.Ready,
        StatusMeaning: JobStatusCode.Ready.Description,
        Headline: "Ready, scheduled to run in 5m.",
        ActiveWait: null,
        Lease: null,
        LastExecutedBy: $"{FakeWorkers.KnownWorkerRef} (version 1.4.0)",
        Steps: [new ExplainJobStep("post-invoice", StepStatusCode.Pending, "has started and has no recorded success")],
        StepsHasMore: false,
        Reason: "Invoice provider timed out.",
        NextActions: [new ExplainJobAction("none", "no action needed - the job becomes claimable when its next run time arrives")]
    );

    // The parent waits on its children, so it is suspended; the job awaits nothing, so the map shows no wait.
    private static readonly GetJobLineageMapResponse Lineage = new(
        Ancestors:
        [
            new GetJobLineageMapNode(
                40,
                ParentJobRef,
                "billing",
                "close-month",
                JobStatusCode.Suspended,
                null,
                null,
                null,
                null,
                At.AddHours(-2),
                At.AddHours(-1)
            ),
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
            At.AddHours(-1),
            At
        ),
        Steps: [new GetJobLineageMapStep("post-invoice", StepStatusCode.Pending, StepStatusCode.Pending.Description)],
        StepsHasMore: false,
        ActiveWait: null,
        Children: [new GetJobLineageMapChild(7, FakeJobs.ChildJobRef, "render-pdf", JobStatusCode.Executing, At.AddMinutes(-30), At)],
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
        DetailsText: """{"to":"ready","reason":"unhandled-exception"}"""
    );

    private static readonly ListAlertsItem Alert = new(
        AlertRef: FakeAlerts.KnownAlertRef,
        AlertId: 9001,
        JobNamespace: "billing",
        JobId: 42,
        JobRef: FakeJobs.FoundJobRef,
        Severity: AlertSeverityCode.Critical,
        Kind: AlertKindCode.FirstFailure,
        Title: "Invoice provider timed out",
        Message: "The provider returned 504 three times.",
        ChannelName: "default",
        OccurrenceCount: 3,
        ResolvedAtUtc: FakeAlerts.ResolvedAt,
        DeliveryStatus: AlertDeliveryStatusCode.Delivered,
        RetryCount: 1,
        RetryAfterUtc: null,
        CreatedAtUtc: At,
        ModifiedAtUtc: FakeAlerts.ResolvedAt,
        AcknowledgedAtUtc: FakeAlerts.AcknowledgedAt
    );

    private static readonly GetAlertResponse? AlertDetail = new(
        Alert.AlertRef,
        Alert.AlertId,
        Alert.JobNamespace,
        Alert.JobId,
        Alert.JobRef,
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
        false,
        At.AddHours(-2),
        At
    );

    private static readonly ListSchedulesItem Schedule = new(
        ScheduleId: 3,
        JobId: 45,
        DefinitionId: 5,
        JobNamespace: "billing",
        JobName: "send-invoice",
        ScheduleName: "morning-run",
        ExpressionKind: ScheduleExpressionKindCode.Cron,
        Expression: "0 10 * * 1-5",
        TimeZoneId: "UTC",
        MisfireStrategy: MisfireStrategyCode.CatchUpOnce,
        NextOccurrenceAtUtc: NextRun,
        NextEvaluationAtUtc: NextRun,
        EvaluationStatus: ScheduleEvaluationStatusCode.Scheduled,
        Status: ScheduleStatusCode.Active,
        PausedUntilUtc: null,
        CreatedAtUtc: At,
        ModifiedAtUtc: At,
        Version: 2,
        JobRef: SlotJobRef,
        MaxOutstanding: 1,
        OutstandingCount: 0,
        FullBehavior: ScheduleFullBehaviorCode.Wait,
        ScheduledDelivery: ScheduledDeliveryCode.Permanent,
        ExpireAfterSeconds: 3600,
        ExpireAction: ScheduleExpireActionCode.Discard,
        AvailabilityCalendarName: "office-hours",
        StartAtUtc: new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        EndBeforeUtc: new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    );

    /// <summary>The listed schedule as its point read answers it, member for member.</summary>
    private static readonly GetScheduleResponse ScheduleDetail = new(
        Schedule.ScheduleId,
        Schedule.JobId,
        Schedule.DefinitionId,
        Schedule.JobNamespace,
        Schedule.JobName,
        Schedule.ScheduleName,
        Schedule.ExpressionKind,
        Schedule.Expression,
        Schedule.TimeZoneId,
        Schedule.MisfireStrategy,
        Schedule.NextOccurrenceAtUtc,
        Schedule.NextEvaluationAtUtc,
        Schedule.EvaluationStatus,
        Schedule.Status,
        Schedule.PausedUntilUtc,
        Schedule.CreatedAtUtc,
        Schedule.ModifiedAtUtc,
        Schedule.Version,
        Schedule.JobRef,
        Schedule.MaxOutstanding,
        Schedule.OutstandingCount,
        Schedule.FullBehavior,
        Schedule.ScheduledDelivery,
        Schedule.ExpireAfterSeconds,
        Schedule.ExpireAction,
        Schedule.AvailabilityCalendarName,
        Schedule.StartAtUtc,
        Schedule.EndBeforeUtc
    );
}
