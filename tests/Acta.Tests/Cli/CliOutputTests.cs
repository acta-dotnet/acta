using System.Reflection;
using System.Text.Json;
using Acta.AspNetCore;
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
        Assert.Contains("action: applied", text);
        Assert.Contains("status: paused", text);
    }

    // One casing for a code on every line a human reads, the wire form the --json output and the API write.
    [Fact]
    public void Plain_lines_spell_each_code_as_the_json_does()
    {
        var plain = new StringWriter();
        var json = new StringWriter();
        var already = new PauseJobResponse(123, ControlAction.AlreadyInState, JobStatusCode.Paused, 4);
        CliOutput.WriteControl(plain, "pause", SampleJobRef, already, json: false);
        CliOutput.WriteControl(json, "pause", SampleJobRef, already, json: true);
        CliOutput.WriteStatus(plain, SampleJobRef, JobStatusCode.Succeeded, json: false);
        CliOutput.WriteDebugRun(plain, SampleJobRef, nameof(RunOnceStatus.NothingClaimed), JobStatusCode.Ready, json: false);

        Assert.Contains("action: already-in-state", plain.ToString());
        Assert.Contains("\"action\":\"already-in-state\"", json.ToString());
        Assert.Contains("status: succeeded", plain.ToString());
        Assert.Contains("run: nothing-claimed", plain.ToString());
        Assert.Contains("status: ready", plain.ToString());
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
        Assert.Contains("\"action\":\"applied\"", text);
        Assert.Contains("\"status\":\"paused\"", text);
    }

    [Fact]
    public void Snapshot_plain_writes_identity_and_status()
    {
        var w = new StringWriter();
        var s = new GetJobResponse(
            JobId: 7,
            JobRef: SampleJobRef,
            Kind: JobKindCode.Ordinary,
            LineageRootJobId: null,
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
        Assert.Contains("status: ready", text);
    }

    [Fact]
    public void Snapshot_json_names_the_job_and_its_instant_as_every_surface_does()
    {
        var w = new StringWriter();
        CliOutput.WriteSnapshot(w, Snapshot(JobStatusCode.Ready, tenantId: null), json: true);
        var text = w.ToString();
        Assert.Contains("\"jobNamespace\":\"shop\"", text);
        Assert.Contains("\"jobName\":\"send-email\"", text);
        Assert.Contains("\"createdAtUtc\":\"2026-06-11T08:00:00Z\"", text);
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
            Kind: JobKindCode.Ordinary,
            LineageRootJobId: null,
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

    // One spelling for an instant on every surface: each CLI line and --json field writes it as the API does.
    [Theory]
    [InlineData(0)]
    [InlineData(1_720_000)]
    [InlineData(1_720_005)]
    public void Every_instant_the_cli_prints_reads_as_the_api_writes_it(long fractionTicks)
    {
        var instant = new DateTime(2026, 11, 8, 4, 47, 22, DateTimeKind.Utc).AddTicks(fractionTicks);
        var api = JsonSerializer.Serialize(instant, typeof(DateTime), ActaApiJsonContext.Default).Trim('"');
        var page = new ListEventsResponse(
            [Event(EventCode.JobFailed) with { CreatedAtUtc = instant }],
            NextCursor: null,
            HasMore: false,
            PageSize: 50,
            TotalCount: null
        );
        var snapshot = Snapshot(JobStatusCode.Ready, tenantId: null) with { CreatedAtUtc = instant };
        var explanation = SentinelExplanation() with
        {
            Lease = SentinelExplanation().Lease! with { ExpiresAtUtc = instant, WorkerLastSeenAtUtc = instant },
        };

        var plain = new StringWriter();
        CliOutput.WriteEvents(plain, SampleJobRef, page, json: false);
        CliOutput.WriteSnapshot(plain, snapshot, json: false);
        CliOutput.WriteExplanation(plain, explanation, json: false);
        var json = new StringWriter();
        CliOutput.WriteEvents(json, SampleJobRef, page, json: true);
        CliOutput.WriteSnapshot(json, snapshot, json: true);
        CliOutput.WriteExplanation(json, explanation, json: true);

        Assert.Contains($"{api}  job.failed", plain.ToString());
        Assert.Contains($"created-utc: {api}{Environment.NewLine}", plain.ToString());
        Assert.Contains($"lease expired at {api}.", plain.ToString());
        Assert.Contains($"- Last heartbeat at {api}.", plain.ToString());
        Assert.Equal(5, json.ToString().Split($"\":\"{api}\"").Length);
    }

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

    private static ListEventsItem Event(EventCode code, string? message = null, string? details = null) =>
        new(
            EventId: 1,
            EventCode: code,
            CreatedAtUtc: new DateTime(2026, 6, 21, 12, 30, 1, DateTimeKind.Utc),
            JobNamespace: "shop",
            EntityKind: EntityCode.Job,
            EntityId: 7,
            JobName: "send-invoice",
            JobRef: JobRef.New(),
            LineageRootJobId: null,
            LineageRootJobRef: null,
            DefinitionId: null,
            TenantId: null,
            TenantName: null,
            WorkerId: 3,
            WorkerRef: WorkerRef.New(),
            ExecutionNumber: 1,
            DurationMs: null,
            Message: message,
            DetailsText: details
        );

    [Fact]
    public void Events_plain_writes_one_line_per_event_with_message_and_details()
    {
        var w = new StringWriter();
        var page = new ListEventsResponse(
            [Event(EventCode.JobResumed), Event(EventCode.JobFailed, "boom in handler", """{"reason":"unhandled-exception"}""")],
            NextCursor: null,
            HasMore: false,
            PageSize: 50,
            TotalCount: null
        );

        CliOutput.WriteEvents(w, SampleJobRef, page, json: false);
        var text = w.ToString();

        Assert.Contains($"Events for job {SampleJobRef}", text);
        Assert.Contains("job.resumed", text);
        Assert.Contains("job.failed", text);
        Assert.Contains("""details: {"reason":"unhandled-exception"}""", text);
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
            [Event(EventCode.JobFailed, "boom", """{"reason":"unhandled-exception"}""")],
            NextCursor: "next-cur",
            HasMore: true,
            PageSize: 50,
            TotalCount: null
        );

        CliOutput.WriteEvents(w, SampleJobRef, page, json: true);
        var text = w.ToString();

        Assert.Contains($"\"jobRef\":\"{SampleJobRef}\"", text);
        Assert.Contains("\"createdAtUtc\":\"2026-06-21T12:30:01Z\"", text);
        Assert.Contains("\"event\":\"job.failed\"", text);
        Assert.Contains("\"message\":\"boom\"", text);
        Assert.Contains("\"detailsText\":", text);
        Assert.Contains("unhandled-exception", text);
        Assert.Contains("\"nextCursor\":\"next-cur\"", text);
    }

    [Fact]
    public void Snapshot_failed_hints_at_events_command()
    {
        var w = new StringWriter();
        var s = new GetJobResponse(
            JobId: 7,
            JobRef: JobRef.New(),
            Kind: JobKindCode.Ordinary,
            LineageRootJobId: null,
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

    private static ExplainJobResponse Explanation() =>
        new(
            JobId: 4821,
            JobRef: SampleJobRef,
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Suspended,
            StatusMeaning: JobStatusCode.Suspended.Description,
            Headline: "Suspended, waiting for signal \"fraud-review\".",
            ActiveWait: new ExplainJobWait(CheckpointKindCode.Signal, "fraud-review", null),
            Lease: null,
            LastExecutedBy: $"{ExplainWorkerRef} (version payments-v42)",
            Steps: [new ExplainJobStep("reserve-stock", StepStatusCode.Succeeded, "succeeded and will not rerun")],
            StepsHasMore: true,
            Reason: null,
            NextActions:
            [
                new ExplainJobAction("raise-signal", "raise signal \"fraud-review\""),
                new ExplainJobAction("cancel", "cancel the job"),
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
        Assert.Contains($"- Last executed on worker {ExplainWorkerRef} (version payments-v42).", text);
        Assert.Contains("Durable work:", text);
        Assert.Contains("- Step \"reserve-stock\" succeeded and will not rerun.", text);
        Assert.Contains("- Older steps are not shown.", text);
        Assert.Contains("Next actions:", text);
        Assert.Contains("- Raise signal \"fraud-review\".", text);
        Assert.Contains("- Cancel the job.", text);
    }

    [Fact]
    public void Explanation_plain_writes_reason_worker_recovery_and_next_actions()
    {
        var w = new StringWriter();
        var explanation = new ExplainJobResponse(
            JobId: 4821,
            JobRef: JobRef.New(),
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Executing,
            StatusMeaning: JobStatusCode.Executing.Description,
            Headline: "Executing, but its lease expired 2m ago.",
            ActiveWait: null,
            Lease: new ExplainJobLease(
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
            StepsHasMore: false,
            Reason: "worker shutdown",
            NextActions:
            [
                new ExplainJobAction("wait-recovery", "wait for crash recovery to reclaim the job on its next pass"),
                new ExplainJobAction("cancel", "cancel the job if it should not continue"),
            ]
        );

        CliOutput.WriteExplanation(w, explanation, json: false);
        var text = w.ToString();

        Assert.Contains($"Reason:{Environment.NewLine}- worker shutdown{Environment.NewLine}", text);
        Assert.Contains("Worker:", text);
        Assert.Contains($"- Worker {ExplainWorkerRef} (version payments-v42), lease expired at 2026-07-04T11:58:00Z.", text);
        Assert.Contains("- Last heartbeat at 2026-07-04T11:56:00Z.", text);
        Assert.Contains("- Worker is marked Dead.", text);
        Assert.Contains("Next actions:", text);
        Assert.Contains("- Wait for crash recovery to reclaim the job on its next pass.", text);
        Assert.Contains("- Cancel the job if it should not continue.", text);
    }

    // The reason is the handler's exception message or a reason code; the CLI prints it as it was written.
    [Fact]
    public void Explanation_plain_prints_the_reason_as_the_handler_wrote_it()
    {
        var w = new StringWriter();
        var failed = Explanation() with { Status = JobStatusCode.Failed, Headline = "Failed.", Reason = "carrier acme-post answered 503" };

        CliOutput.WriteExplanation(w, failed, json: false);

        Assert.Contains($"Reason:{Environment.NewLine}- carrier acme-post answered 503{Environment.NewLine}", w.ToString());
    }

    [Fact]
    public void Explanation_plain_writes_timer_wait_as_executor_free_durable_work()
    {
        var w = new StringWriter();
        var explanation = new ExplainJobResponse(
            JobId: 4821,
            JobRef: JobRef.New(),
            JobNamespace: "emails",
            JobName: "send-follow-up",
            Status: JobStatusCode.Suspended,
            StatusMeaning: JobStatusCode.Suspended.Description,
            Headline: "Suspended on durable sleep \"cool-down\", due in 12m.",
            ActiveWait: new ExplainJobWait(CheckpointKindCode.Timer, "cool-down", new DateTime(2026, 7, 4, 12, 12, 0, DateTimeKind.Utc)),
            Lease: null,
            LastExecutedBy: null,
            Steps: [],
            StepsHasMore: false,
            Reason: null,
            NextActions: [new ExplainJobAction("none", "no action needed - it resumes when the timer is due")]
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
        Assert.Contains("\"jobNamespace\":\"payments\"", text);
        Assert.Contains("\"jobName\":\"checkout\"", text);
        Assert.Contains("\"status\":\"suspended\"", text);
        Assert.Contains("\"headline\":", text);
        Assert.Contains("\"activeWait\":", text);
        Assert.Contains("\"kind\":\"signal\"", text);
        Assert.Contains("\"stepsHasMore\":true", text);
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
        Assert.Contains("migrate", text);
        Assert.Contains("shop", text);
        Assert.Contains("billing", text);
        Assert.Contains("Exit codes: 0 applied/found, 1 rejected/failed, 2 not found, 64 usage error, 130 cancelled (Ctrl-C).", text);
    }

    [Fact]
    public void Usage_with_no_namespaces_prints_none()
    {
        var w = new StringWriter();
        CliOutput.WriteUsage(w, []);
        Assert.Contains("Registered namespaces: (none)", w.ToString());
    }

    [Fact]
    public void Migrate_plain_names_each_applied_migration_or_says_the_schema_is_up_to_date()
    {
        var applied = new StringWriter();
        CliOutput.WriteMigrate(applied, "acta", ["M20260101000000_init", "M20260102000000_add_flags"], [], json: false);
        Assert.Equal(
            ["applied M20260101000000_init to schema acta", "applied M20260102000000_add_flags to schema acta"],
            applied.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        );

        var current = new StringWriter();
        CliOutput.WriteMigrate(current, "acta", [], [], json: false);
        Assert.Equal("schema acta is up to date", current.ToString().Trim());
    }

    [Fact]
    public void Migrate_plain_prints_the_deploy_after_the_schema()
    {
        var w = new StringWriter();
        CliOutput.WriteMigrate(w, "acta", [], [DeployedNamespace], json: false);
        Assert.Equal(
            [
                "schema acta is up to date",
                "namespace billing: 1.6.1+b81d04e (Production)",
                "  added    send-receipt",
                "  changed  send-invoice",
                "  12 unchanged",
                "  not declared by this build: old-report",
            ],
            w.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        );
    }

    [Fact]
    public void Migrate_json_writes_the_schema_the_applied_names_and_the_namespaces()
    {
        var w = new StringWriter();
        CliOutput.WriteMigrate(w, "acta", ["M20260101000000_init"], [], json: true);
        Assert.Equal("{\"schema\":\"acta\",\"applied\":[\"M20260101000000_init\"],\"namespaces\":[]}", w.ToString().Trim());
    }

    private static readonly NamespaceDeployment DeployedNamespace = new(
        "billing",
        "1.6.1+b81d04e",
        "Production",
        "1.6.1+b81d04e",
        IsOlder: false,
        Added: ["send-receipt"],
        Changed: ["send-invoice"],
        Unchanged: 12,
        Undeclared: ["old-report"]
    );

    private static readonly NamespaceDeployment OlderNamespace = new(
        "billing",
        "1.6.0+77c0a12",
        "Production",
        "1.6.1+b81d04e",
        IsOlder: true,
        Added: [],
        Changed: [],
        Unchanged: 0,
        Undeclared: []
    );

    [Fact]
    public void Deploy_plain_says_an_older_build_changed_nothing_and_names_the_flag()
    {
        var w = new StringWriter();
        CliOutput.WriteDeploy(w, [OlderNamespace], json: false);
        Assert.Equal(
            [
                "namespace billing: deployed at 1.6.1+b81d04e",
                "  this build (1.6.0+77c0a12) deployed earlier; nothing changed",
                "  use --force to deploy it",
            ],
            w.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        );
    }

    [Fact]
    public void Deploy_plain_leaves_out_an_unknown_environment_and_an_empty_undeclared_list()
    {
        var w = new StringWriter();
        CliOutput.WriteDeploy(
            w,
            [DeployedNamespace with { EnvironmentName = null, Added = [], Changed = [], Undeclared = [] }],
            json: false
        );
        Assert.Equal(
            ["namespace billing: 1.6.1+b81d04e", "  12 unchanged"],
            w.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        );
    }

    [Fact]
    public void Deploy_plain_says_so_when_the_host_runs_no_worker()
    {
        var w = new StringWriter();
        CliOutput.WriteDeploy(w, [], json: false);
        Assert.Equal("no worker namespace is registered in this host; nothing to deploy", w.ToString().Trim());
    }

    [Fact]
    public void Deploy_json_writes_each_namespace_with_what_changed()
    {
        var w = new StringWriter();
        CliOutput.WriteDeploy(w, [DeployedNamespace, OlderNamespace], json: true);
        Assert.Equal(
            "{\"namespaces\":["
                + "{\"namespace\":\"billing\",\"deploymentVersion\":\"1.6.1\\u002Bb81d04e\",\"environment\":\"Production\","
                + "\"currentDeploymentVersion\":\"1.6.1\\u002Bb81d04e\",\"older\":false,\"added\":[\"send-receipt\"],"
                + "\"changed\":[\"send-invoice\"],\"unchanged\":12,\"undeclared\":[\"old-report\"]},"
                + "{\"namespace\":\"billing\",\"deploymentVersion\":\"1.6.0\\u002B77c0a12\",\"environment\":\"Production\","
                + "\"currentDeploymentVersion\":\"1.6.1\\u002Bb81d04e\",\"older\":true,\"added\":[],\"changed\":[],\"unchanged\":0,"
                + "\"undeclared\":[]}]}",
            w.ToString().Trim()
        );
    }

    [Fact]
    public void DebugRun_prints_outcome_and_handles_null_status()
    {
        var plain = new StringWriter();
        CliOutput.WriteDebugRun(plain, SampleJobRef, "Completed", JobStatusCode.Succeeded, json: false);
        Assert.Contains("run: completed", plain.ToString());
        Assert.Contains("status: succeeded", plain.ToString());

        var json = new StringWriter();
        CliOutput.WriteDebugRun(json, SampleJobRef, "Rearmed", null, json: true);
        Assert.Contains("\"run\":\"rearmed\"", json.ToString());
        Assert.Contains("\"status\":null", json.ToString());
    }

    // Every internal id a CLI fixture can carry is a distinctive sentinel, so a writer that prints
    // one is caught by the digits themselves - not by a field name a rename could slip past. The
    // counterpart assertion is that the ref or key the operator addresses the row by IS printed, so
    // the gate cannot be satisfied by writing nothing. Each writer runs plain and --json.

    private const long SentinelJobId = 424242;
    private const long SentinelLineageRootJobId = 424243;
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
        // Usage text lists verbs and namespace names, migrate prints a schema and migration names, and
        // deploy prints namespace names, job names and versions; none renders an entity, so none carries
        // an identity to leak. Listed here so the surface comparison stays exact rather than filtered.
        nameof(CliOutput.WriteDeploy),
        nameof(CliOutput.WriteMigrate),
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
            Kind: JobKindCode.Ordinary,
            LineageRootJobId: SentinelLineageRootJobId,
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
                    EventId: SentinelJobEventId,
                    EventCode: EventCode.JobFailed,
                    CreatedAtUtc: new DateTime(2026, 6, 21, 12, 30, 1, DateTimeKind.Utc),
                    JobNamespace: "shop",
                    EntityKind: EntityCode.Job,
                    EntityId: SentinelJobId,
                    JobName: "send-invoice",
                    JobRef: SampleJobRef,
                    LineageRootJobId: SentinelLineageRootJobId,
                    LineageRootJobRef: SentinelLineageRootJobRef,
                    DefinitionId: SentinelDefinitionId,
                    TenantId: SentinelTenantId,
                    TenantName: SentinelTenantName,
                    WorkerId: SentinelWorkerId,
                    WorkerRef: SentinelWorkerRef,
                    ExecutionNumber: 2,
                    DurationMs: 12,
                    Message: "boom",
                    DetailsText: """{"reason":"unhandled-exception"}"""
                ),
            ],
            NextCursor: SentinelCursor,
            HasMore: true,
            PageSize: 50,
            TotalCount: null
        );

    private static ExplainJobResponse SentinelExplanation() =>
        new(
            JobId: SentinelJobId,
            JobRef: SampleJobRef,
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Executing,
            StatusMeaning: JobStatusCode.Executing.Description,
            Headline: "Executing, but its lease expired 2m ago.",
            ActiveWait: null,
            Lease: new ExplainJobLease(
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
            Steps: [new ExplainJobStep("reserve-stock", StepStatusCode.Succeeded, "succeeded and will not rerun")],
            StepsHasMore: false,
            Reason: "worker shutdown",
            NextActions: [new ExplainJobAction("cancel", "cancel the job")]
        );
}
