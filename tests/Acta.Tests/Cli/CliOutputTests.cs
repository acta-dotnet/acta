using System.Reflection;
using Xunit;

namespace Acta.Tests.Cli;

public class CliOutputTests
{
    private static readonly WorkerRef ExplainWorkerRef = new(new Guid("019826f0-0000-7000-8000-000000000011"));
    private static readonly JobRef SampleJobRef = new(new Guid("019826f0-0000-7000-8000-000000000042"));

    [Fact]
    public void Control_plain_writes_key_value_lines()
    {
        var w = new StringWriter();
        CliOutput.WriteControl(
            w,
            "pause",
            SampleJobRef,
            new PauseJobResponse(123, ControlAction.Applied, JobStatusCode.Paused, 4),
            json: false
        );
        var text = w.ToString();
        Assert.Contains($"job: {SampleJobRef}", text);
        Assert.Contains("action: Applied", text);
        Assert.Contains("status: Paused", text);
    }

    [Fact]
    public void Control_json_writes_wire_record()
    {
        var w = new StringWriter();
        CliOutput.WriteControl(
            w,
            "pause",
            SampleJobRef,
            new PauseJobResponse(123, ControlAction.Applied, JobStatusCode.Paused, 4),
            json: true
        );
        var text = w.ToString();
        Assert.Contains($"\"jobRef\":\"{SampleJobRef}\"", text);
        Assert.DoesNotContain("\"jobId\"", text);
        Assert.Contains("\"action\":\"Applied\"", text);
        Assert.Contains("\"status\":\"Paused\"", text);
    }

    [Fact]
    public void Snapshot_plain_writes_identity_and_status()
    {
        var w = new StringWriter();
        var s = new GetJobResponse(
            JobId: 7,
            JobRef: SampleJobRef,
            LineageRootId: null,
            LineageRootJobRef: null,
            ParentJobId: null,
            ParentJobRef: null,
            DeduplicationKey: null,
            CorrelationKey: null,
            JobNamespace: "shop",
            JobName: "send-email",
            DefinitionId: 11,
            TenantId: null,
            TenantName: null,
            Status: JobStatusCode.Ready,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            ExecutionNumber: 0,
            FailureCount: 0,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ConcurrencyKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            LeasedByWorkerRef: null,
            Version: 3
        );
        CliOutput.WriteSnapshot(w, s, json: false);
        var text = w.ToString();
        Assert.Contains($"job: {SampleJobRef}", text);
        Assert.DoesNotContain("job: 7", text);
        Assert.Contains("namespace: shop", text);
        Assert.Contains("name: send-email", text);
        Assert.Contains("status: Ready", text);
    }

    [Fact]
    public void Snapshot_renders_tenant_when_present_and_none_when_absent()
    {
        var withTenant = Snapshot(JobStatusCode.Ready, tenantId: 42);
        var plain = new StringWriter();
        CliOutput.WriteSnapshot(plain, withTenant, json: false);
        Assert.Contains("tenant: tenant-42", plain.ToString());

        var json = new StringWriter();
        CliOutput.WriteSnapshot(json, withTenant, json: true);
        Assert.Contains("\"tenantName\":\"tenant-42\"", json.ToString());
        Assert.DoesNotContain("\"tenantId\"", json.ToString());

        var noTenant = Snapshot(JobStatusCode.Ready, tenantId: null);
        var plainNone = new StringWriter();
        CliOutput.WriteSnapshot(plainNone, noTenant, json: false);
        Assert.Contains("tenant: (none)", plainNone.ToString());

        var jsonNull = new StringWriter();
        CliOutput.WriteSnapshot(jsonNull, noTenant, json: true);
        Assert.Contains("\"tenantName\":null", jsonNull.ToString());
    }

    private static GetJobResponse Snapshot(JobStatusCode status, int? tenantId) =>
        new(
            JobId: 7,
            JobRef: JobRef.New(),
            LineageRootId: null,
            LineageRootJobRef: null,
            ParentJobId: null,
            ParentJobRef: null,
            DeduplicationKey: null,
            CorrelationKey: null,
            JobNamespace: "shop",
            JobName: "send-email",
            DefinitionId: 11,
            TenantId: tenantId,
            TenantName: tenantId is null ? null : "tenant-" + tenantId,
            Status: status,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            ExecutionNumber: 0,
            FailureCount: 0,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ConcurrencyKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            LeasedByWorkerRef: null,
            Version: 3
        );

    [Fact]
    public void Control_null_status_prints_none_and_json_null()
    {
        var plain = new StringWriter();
        CliOutput.WriteControl(plain, "cancel", SampleJobRef, new CancelJobResponse(0, ControlAction.NotFound, null, null), json: false);
        Assert.Contains("status: (none)", plain.ToString());

        var json = new StringWriter();
        CliOutput.WriteControl(json, "cancel", SampleJobRef, new CancelJobResponse(0, ControlAction.NotFound, null, null), json: true);
        Assert.Contains("\"status\":null", json.ToString());
    }

    private static ListEventsItem Event(
        EventCode code,
        JobStatusCode? from,
        JobStatusCode? to,
        EventReasonCode? reason = null,
        string? message = null,
        string? detail = null
    ) =>
        new(
            JobEventId: 1,
            EventCode: code,
            CreatedAtUtc: new DateTime(2026, 6, 21, 12, 30, 1, DateTimeKind.Utc),
            JobNamespace: "shop",
            JobName: "send-invoice",
            JobId: 7,
            JobRef: JobRef.New(),
            LineageRootId: null,
            LineageRootJobRef: null,
            DefinitionId: null,
            TenantId: null,
            WorkerId: 3,
            ExecutionNumber: 1,
            ActorCode: ActorCode.Worker,
            Actor: null,
            FromStatus: from,
            ToStatus: to,
            ExecutionStatus: null,
            DurationMs: null,
            ReasonCode: reason,
            ReasonMessage: message,
            DetailText: detail,
            WorkerRef: WorkerRef.New(),
            TenantName: null
        );

    [Fact]
    public void Events_plain_writes_one_line_per_event_with_reason_detail()
    {
        var w = new StringWriter();
        var page = new ListEventsResponse(
            [
                Event(EventCode.JobResumed, JobStatusCode.Paused, JobStatusCode.Ready),
                Event(
                    EventCode.JobExecutionFinished,
                    JobStatusCode.Executing,
                    JobStatusCode.Failed,
                    EventReasonCode.JobUnhandledException,
                    "boom in handler"
                ),
            ],
            NextCursor: null,
            HasMore: false,
            PageSize: 50,
            TotalCount: null
        );

        CliOutput.WriteEvents(w, SampleJobRef, page, json: false);
        var text = w.ToString();

        Assert.Contains($"Events for job {SampleJobRef}", text);
        Assert.Contains("job.resumed  paused -> ready", text);
        Assert.Contains("job.execution-finished  executing -> failed", text);
        Assert.Contains("reason: job.unhandled-exception", text);
        Assert.Contains("message: boom in handler", text);
    }

    [Fact]
    public void Events_plain_empty_page_prints_no_events()
    {
        var w = new StringWriter();
        var page = new ListEventsResponse([], NextCursor: null, HasMore: false, PageSize: 50, TotalCount: null);
        CliOutput.WriteEvents(w, SampleJobRef, page, json: false);
        Assert.Contains("(no events)", w.ToString());
    }

    [Fact]
    public void Events_json_writes_event_array_and_next_cursor()
    {
        var w = new StringWriter();
        var page = new ListEventsResponse(
            [
                Event(
                    EventCode.JobExecutionFinished,
                    JobStatusCode.Executing,
                    JobStatusCode.Failed,
                    EventReasonCode.JobUnhandledException,
                    "boom"
                ),
            ],
            NextCursor: "next-cur",
            HasMore: true,
            PageSize: 50,
            TotalCount: null
        );

        CliOutput.WriteEvents(w, SampleJobRef, page, json: true);
        var text = w.ToString();

        Assert.Contains($"\"jobRef\":\"{SampleJobRef}\"", text);
        Assert.Contains("\"event\":\"job.execution-finished\"", text);
        Assert.Contains("\"reasonCode\":\"job.unhandled-exception\"", text);
        Assert.Contains("\"reasonMessage\":\"boom\"", text);
        Assert.Contains("\"nextCursor\":\"next-cur\"", text);
    }

    [Fact]
    public void Snapshot_failed_hints_at_events_command()
    {
        var w = new StringWriter();
        var s = new GetJobResponse(
            JobId: 7,
            JobRef: JobRef.New(),
            LineageRootId: null,
            LineageRootJobRef: null,
            ParentJobId: null,
            ParentJobRef: null,
            DeduplicationKey: null,
            CorrelationKey: null,
            JobNamespace: "shop",
            JobName: "send-email",
            DefinitionId: 11,
            TenantId: null,
            TenantName: null,
            Status: JobStatusCode.Failed,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            ExecutionNumber: 1,
            FailureCount: 3,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ConcurrencyKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            LeasedByWorkerRef: null,
            Version: 3
        );
        CliOutput.WriteSnapshot(w, s, json: false);
        Assert.Contains("events: run 'jobs events", w.ToString());
    }

    private static GetJobExplanationResponse Explanation() =>
        new(
            JobId: 4821,
            JobRef: SampleJobRef,
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Suspended,
            StatusMeaning: JobStatusCode.Suspended.Description,
            Headline: "Suspended, waiting for signal \"fraud-review\".",
            ActiveWait: new GetJobExplanationWait(CheckpointKindCode.Signal, "fraud-review", null),
            Lease: null,
            LastExecutedBy: "payments-v42",
            Steps: [new GetJobExplanationStep("reserve-stock", StepStatusCode.Succeeded, "succeeded and will not rerun")],
            Reason: null,
            NextActions:
            [
                new GetJobExplanationAction("raise-signal", "raise signal \"fraud-review\""),
                new GetJobExplanationAction("cancel", "cancel the job"),
            ]
        );

    [Fact]
    public void Explanation_plain_writes_headline_steps_and_next_action()
    {
        var w = new StringWriter();
        CliOutput.WriteExplanation(w, Explanation(), json: false);
        var text = w.ToString();

        Assert.Contains("payments/checkout", text);
        Assert.Contains("Suspended, waiting for signal \"fraud-review\".", text);
        Assert.Contains("Last activity:", text);
        Assert.Contains("- Last executed on worker payments-v42.", text);
        Assert.Contains("Durable work:", text);
        Assert.Contains("- Step \"reserve-stock\" succeeded and will not rerun.", text);
        Assert.Contains("Next actions:", text);
        Assert.Contains("- Raise signal \"fraud-review\".", text);
        Assert.Contains("- Cancel the job.", text);
    }

    [Fact]
    public void Explanation_plain_writes_reason_worker_recovery_and_next_actions()
    {
        var w = new StringWriter();
        var explanation = new GetJobExplanationResponse(
            JobId: 4821,
            JobRef: JobRef.New(),
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Executing,
            StatusMeaning: JobStatusCode.Executing.Description,
            Headline: "Executing, but its lease expired 2m ago.",
            ActiveWait: null,
            Lease: new GetJobExplanationLease(
                WorkerId: 17,
                WorkerName: "payments-v42",
                ExpiresAtUtc: new DateTime(2026, 7, 4, 11, 58, 0, DateTimeKind.Utc),
                Expired: true,
                WorkerLastSeenAtUtc: new DateTime(2026, 7, 4, 11, 56, 0, DateTimeKind.Utc),
                WorkerStale: true,
                RecoveryExpectation: "Recovery should return it to Ready on its next pass.",
                WorkerRef: ExplainWorkerRef
            ),
            LastExecutedBy: null,
            Steps: [],
            Reason: "worker shutdown",
            NextActions:
            [
                new GetJobExplanationAction("wait-recovery", "wait for crash recovery to reclaim the job on its next pass"),
                new GetJobExplanationAction("cancel", "cancel the job if it should not continue"),
            ]
        );

        CliOutput.WriteExplanation(w, explanation, json: false);
        var text = w.ToString();

        Assert.Contains("Reason:", text);
        Assert.Contains("- Worker shutdown.", text);
        Assert.Contains("Worker:", text);
        Assert.Contains($"- Worker payments-v42 ({ExplainWorkerRef}), lease expired at 2026-07-04T11:58:00.0000000Z.", text);
        Assert.Contains("- Last heartbeat at 2026-07-04T11:56:00.0000000Z.", text);
        Assert.Contains("- Worker is marked Dead.", text);
        Assert.Contains("Next actions:", text);
        Assert.Contains("- Wait for crash recovery to reclaim the job on its next pass.", text);
        Assert.Contains("- Cancel the job if it should not continue.", text);
    }

    [Fact]
    public void Explanation_plain_writes_timer_wait_as_executor_free_durable_work()
    {
        var w = new StringWriter();
        var explanation = new GetJobExplanationResponse(
            JobId: 4821,
            JobRef: JobRef.New(),
            JobNamespace: "emails",
            JobName: "send-follow-up",
            Status: JobStatusCode.Suspended,
            StatusMeaning: JobStatusCode.Suspended.Description,
            Headline: "Suspended on durable sleep \"cool-down\", due in 12m.",
            ActiveWait: new GetJobExplanationWait(
                CheckpointKindCode.Timer,
                "cool-down",
                new DateTime(2026, 7, 4, 12, 12, 0, DateTimeKind.Utc)
            ),
            Lease: null,
            LastExecutedBy: null,
            Steps: [],
            Reason: null,
            NextActions: [new GetJobExplanationAction("none", "no action needed - it resumes when the timer is due")]
        );

        CliOutput.WriteExplanation(w, explanation, json: false);
        var text = w.ToString();

        Assert.Contains("emails/send-follow-up", text);
        Assert.Contains("Suspended on durable sleep \"cool-down\", due in 12m.", text);
        Assert.Contains("Durable work:", text);
        Assert.Contains("- The job holds no executor while waiting.", text);
        Assert.Contains("Next action:", text);
        Assert.Contains("- No action needed - it resumes when the timer is due.", text);
    }

    [Fact]
    public void Explanation_json_writes_structured_fields()
    {
        var w = new StringWriter();
        CliOutput.WriteExplanation(w, Explanation(), json: true);
        var text = w.ToString();

        Assert.Contains($"\"jobRef\":\"{SampleJobRef}\"", text);
        Assert.DoesNotContain("\"jobId\"", text);
        Assert.Contains("\"status\":\"Suspended\"", text);
        Assert.Contains("\"headline\":", text);
        Assert.Contains("\"activeWait\":", text);
        Assert.Contains("\"kind\":\"Signal\"", text);
        Assert.Contains("\"nextActions\":", text);
        Assert.Contains("\"kind\":\"raise-signal\"", text);
    }

    [Fact]
    public void Usage_lists_verbs_and_namespaces()
    {
        var w = new StringWriter();
        CliOutput.WriteUsage(w, ["shop", "billing"]);
        var text = w.ToString();
        Assert.Contains("pause", text);
        Assert.Contains("debug", text);
        Assert.Contains("events", text);
        Assert.Contains("shop", text);
        Assert.Contains("billing", text);
    }

    [Fact]
    public void Usage_with_no_namespaces_prints_none()
    {
        var w = new StringWriter();
        CliOutput.WriteUsage(w, []);
        Assert.Contains("Registered namespaces: (none)", w.ToString());
    }

    [Fact]
    public void DebugRun_prints_outcome_and_handles_null_status()
    {
        var plain = new StringWriter();
        CliOutput.WriteDebugRun(plain, SampleJobRef, "Completed", JobStatusCode.Succeeded, json: false);
        Assert.Contains("run: Completed", plain.ToString());
        Assert.Contains("status: Succeeded", plain.ToString());

        var json = new StringWriter();
        CliOutput.WriteDebugRun(json, SampleJobRef, "Rearmed", null, json: true);
        Assert.Contains("\"run\":\"Rearmed\"", json.ToString());
        Assert.Contains("\"status\":null", json.ToString());
    }

    // Every internal id a CLI fixture can carry is a distinctive sentinel, so a writer that prints
    // one is caught by the digits themselves - not by a field name a rename could slip past. The
    // counterpart assertion is that the ref or key the operator addresses the row by IS printed, so
    // the gate cannot be satisfied by writing nothing. Each writer runs plain and --json.

    private const long SentinelJobId = 424242;
    private const long SentinelLineageRootId = 424243;
    private const long SentinelParentJobId = 424244;
    private const int SentinelDefinitionId = 424245;
    private const int SentinelTenantId = 424246;
    private const int SentinelWorkerId = 424247;
    private const long SentinelJobEventId = 424248;
    private const int SentinelLeasedByWorkerId = 424249;

    private static readonly string[] InternalIdSentinels = ["424242", "424243", "424244", "424245", "424246", "424247", "424248", "424249"];

    // Fixed, never JobRef.New(): a contract test that mints a random ref has a nonzero chance of
    // rendering a sentinel digit run and failing for a reason that has nothing to do with the contract.
    private static readonly WorkerRef SentinelWorkerRef = new(new Guid("019826f0-0000-7000-8000-0000000000aa"));
    private static readonly JobRef SentinelLineageRootJobRef = new(new Guid("019826f0-0000-7000-8000-0000000000ab"));
    private static readonly JobRef SentinelParentJobRef = new(new Guid("019826f0-0000-7000-8000-0000000000ac"));
    private const string SentinelTenantName = "acme-eu";
    private const string SentinelCursor = "cursor-token";

    // The writer surface is walked rather than trusted: a Write* method added tomorrow lands in none of
    // the theories below and would ship unprotected. Same philosophy as the openapi gate - enumerate the
    // real surface and compare it to a literal, so the gate fails on the day the surface grows.
    private static readonly string[] CoveredWriters =
    [
        nameof(CliOutput.WriteControl),
        nameof(CliOutput.WriteDebugRun),
        nameof(CliOutput.WriteEvents),
        nameof(CliOutput.WriteExplanation),
        nameof(CliOutput.WriteSnapshot),
        nameof(CliOutput.WriteStatus),
        // Usage text lists verbs and namespace names and renders no entity, so it carries no identity
        // to leak. Listed here so the surface comparison stays exact rather than filtered.
        nameof(CliOutput.WriteUsage),
    ];

    [Fact]
    public void Every_cli_writer_is_covered_by_the_no_integer_identity_theories()
    {
        var actual = typeof(CliOutput)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Write", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal);

        // Set equality both ways: a new writer fails as an uncovered addition, and a deleted one fails
        // as a stale entry, so the literal cannot rot in either direction.
        Assert.Equal(CoveredWriters.OrderBy(n => n, StringComparer.Ordinal), actual);
    }

    private static void AssertRefsOnly(string text, params string[] expected)
    {
        foreach (var sentinel in InternalIdSentinels)
        {
            Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        }
        foreach (var value in expected)
        {
            Assert.Contains(value, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Control_output_names_the_job_by_ref_only(bool json)
    {
        var w = new StringWriter();
        CliOutput.WriteControl(
            w,
            "pause",
            SampleJobRef,
            new PauseJobResponse(SentinelJobId, ControlAction.Applied, JobStatusCode.Paused, 4),
            json
        );
        AssertRefsOnly(w.ToString(), SampleJobRef.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Status_output_names_the_job_by_ref_only(bool json)
    {
        var w = new StringWriter();
        CliOutput.WriteStatus(w, SampleJobRef, JobStatusCode.Executing, json);
        AssertRefsOnly(w.ToString(), SampleJobRef.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DebugRun_output_names_the_job_by_ref_only(bool json)
    {
        var w = new StringWriter();
        CliOutput.WriteDebugRun(w, SampleJobRef, "Completed", JobStatusCode.Succeeded, json);
        AssertRefsOnly(w.ToString(), SampleJobRef.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_output_carries_the_ref_and_tenant_name_but_no_internal_id(bool json)
    {
        var w = new StringWriter();
        CliOutput.WriteSnapshot(w, SentinelSnapshot(), json);
        AssertRefsOnly(w.ToString(), SampleJobRef.ToString(), SentinelTenantName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Events_output_carries_job_and_worker_refs_but_no_internal_id(bool json)
    {
        var w = new StringWriter();
        CliOutput.WriteEvents(w, SampleJobRef, SentinelEventPage(), json);
        AssertRefsOnly(w.ToString(), SampleJobRef.ToString(), SentinelWorkerRef.ToString(), SentinelCursor);
    }

    // The continuation hint is the one place the CLI hands an operator a command to retype, so it has
    // to carry the ref: a hint spelling the internal id would teach the retired addressing form.
    [Fact]
    public void The_events_continuation_hint_carries_the_ref_through_to_the_next_page()
    {
        var w = new StringWriter();
        CliOutput.WriteEvents(w, SampleJobRef, SentinelEventPage(), json: false);
        AssertRefsOnly(w.ToString(), $"jobs events {SampleJobRef} --after {SentinelCursor}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explanation_output_carries_job_and_worker_refs_but_no_internal_id(bool json)
    {
        var w = new StringWriter();
        CliOutput.WriteExplanation(w, SentinelExplanation(), json);
        AssertRefsOnly(w.ToString(), SampleJobRef.ToString(), SentinelWorkerRef.ToString());
    }

    private static GetJobResponse SentinelSnapshot() =>
        new(
            JobId: SentinelJobId,
            JobRef: SampleJobRef,
            LineageRootId: SentinelLineageRootId,
            LineageRootJobRef: SentinelLineageRootJobRef,
            ParentJobId: SentinelParentJobId,
            ParentJobRef: SentinelParentJobRef,
            DeduplicationKey: "invoice-7",
            CorrelationKey: null,
            JobNamespace: "shop",
            JobName: "send-email",
            DefinitionId: SentinelDefinitionId,
            TenantId: SentinelTenantId,
            TenantName: SentinelTenantName,
            Status: JobStatusCode.Failed,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            ExecutionNumber: 2,
            FailureCount: 3,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: SentinelLeasedByWorkerId,
            LeaseExpiresAtUtc: null,
            ConcurrencyKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            LeasedByWorkerRef: SentinelWorkerRef,
            Version: 3
        );

    private static ListEventsResponse SentinelEventPage() =>
        new(
            [
                new ListEventsItem(
                    JobEventId: SentinelJobEventId,
                    EventCode: EventCode.JobExecutionFinished,
                    CreatedAtUtc: new DateTime(2026, 6, 21, 12, 30, 1, DateTimeKind.Utc),
                    JobNamespace: "shop",
                    JobName: "send-invoice",
                    JobId: SentinelJobId,
                    JobRef: SampleJobRef,
                    LineageRootId: SentinelLineageRootId,
                    LineageRootJobRef: SentinelLineageRootJobRef,
                    DefinitionId: SentinelDefinitionId,
                    TenantId: SentinelTenantId,
                    WorkerId: SentinelWorkerId,
                    ExecutionNumber: 2,
                    ActorCode: ActorCode.Worker,
                    Actor: null,
                    FromStatus: JobStatusCode.Executing,
                    ToStatus: JobStatusCode.Failed,
                    ExecutionStatus: null,
                    DurationMs: 12,
                    ReasonCode: EventReasonCode.JobUnhandledException,
                    ReasonMessage: "boom",
                    DetailText: null,
                    WorkerRef: SentinelWorkerRef,
                    TenantName: SentinelTenantName
                ),
            ],
            NextCursor: SentinelCursor,
            HasMore: true,
            PageSize: 50,
            TotalCount: null
        );

    private static GetJobExplanationResponse SentinelExplanation() =>
        new(
            JobId: SentinelJobId,
            JobRef: SampleJobRef,
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Executing,
            StatusMeaning: JobStatusCode.Executing.Description,
            Headline: "Executing, but its lease expired 2m ago.",
            ActiveWait: null,
            Lease: new GetJobExplanationLease(
                WorkerId: SentinelWorkerId,
                WorkerName: null,
                ExpiresAtUtc: new DateTime(2026, 7, 4, 11, 58, 0, DateTimeKind.Utc),
                Expired: true,
                WorkerLastSeenAtUtc: new DateTime(2026, 7, 4, 11, 56, 0, DateTimeKind.Utc),
                WorkerStale: true,
                RecoveryExpectation: "Recovery should return it to Ready on its next pass.",
                WorkerRef: SentinelWorkerRef
            ),
            LastExecutedBy: null,
            Steps: [new GetJobExplanationStep("reserve-stock", StepStatusCode.Succeeded, "succeeded and will not rerun")],
            Reason: "worker shutdown",
            NextActions: [new GetJobExplanationAction("cancel", "cancel the job")]
        );
}
