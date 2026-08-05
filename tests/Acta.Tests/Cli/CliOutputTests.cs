using Acta.Runtime.Cli;
using Xunit;

namespace Acta.Tests.Cli;

public class CliOutputTests
{
    [Fact]
    public void Control_plain_writes_key_value_lines()
    {
        var w = new StringWriter();
        CliOutput.WriteControl(w, "pause", new JobControlResult(123, JobControlAction.Applied, JobStatusCode.Paused), json: false);
        var text = w.ToString();
        Assert.Contains("job: 123", text);
        Assert.Contains("action: Applied", text);
        Assert.Contains("status: Paused", text);
    }

    [Fact]
    public void Control_json_writes_wire_record()
    {
        var w = new StringWriter();
        CliOutput.WriteControl(w, "pause", new JobControlResult(123, JobControlAction.Applied, JobStatusCode.Paused), json: true);
        var text = w.ToString();
        Assert.Contains("\"jobId\":123", text);
        Assert.Contains("\"action\":\"Applied\"", text);
        Assert.Contains("\"status\":\"Paused\"", text);
    }

    [Fact]
    public void Snapshot_plain_writes_identity_and_status()
    {
        var w = new StringWriter();
        var s = new JobSnapshot(
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
            JobDefinitionId: 11,
            TenantId: null,
            TenantKey: null,
            Status: JobStatusCode.Ready,
            Priority: JobPriorityCode.Normal,
            ExecutionNumber: 0,
            FailureCount: 0,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ExclusiveKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc)
        );
        CliOutput.WriteSnapshot(w, s, json: false);
        var text = w.ToString();
        Assert.Contains("job: 7", text);
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
        Assert.Contains("tenant: 42", plain.ToString());

        var json = new StringWriter();
        CliOutput.WriteSnapshot(json, withTenant, json: true);
        Assert.Contains("\"tenantId\":42", json.ToString());

        var noTenant = Snapshot(JobStatusCode.Ready, tenantId: null);
        var plainNone = new StringWriter();
        CliOutput.WriteSnapshot(plainNone, noTenant, json: false);
        Assert.Contains("tenant: (none)", plainNone.ToString());

        var jsonNull = new StringWriter();
        CliOutput.WriteSnapshot(jsonNull, noTenant, json: true);
        Assert.Contains("\"tenantId\":null", jsonNull.ToString());
    }

    private static JobSnapshot Snapshot(JobStatusCode status, int? tenantId) =>
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
            JobDefinitionId: 11,
            TenantId: tenantId,
            TenantKey: tenantId is null ? null : "tenant-" + tenantId,
            Status: status,
            Priority: JobPriorityCode.Normal,
            ExecutionNumber: 0,
            FailureCount: 0,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ExclusiveKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc)
        );

    [Fact]
    public void Control_null_status_prints_none_and_json_null()
    {
        var plain = new StringWriter();
        CliOutput.WriteControl(plain, "cancel", new JobControlResult(0, JobControlAction.NotFound, null), json: false);
        Assert.Contains("status: (none)", plain.ToString());

        var json = new StringWriter();
        CliOutput.WriteControl(json, "cancel", new JobControlResult(0, JobControlAction.NotFound, null), json: true);
        Assert.Contains("\"status\":null", json.ToString());
    }

    private static JobEventListItem Event(
        JobEventCode code,
        JobStatusCode? from,
        JobStatusCode? to,
        JobEventReasonCode? reason = null,
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
            JobDefinitionId: null,
            TenantId: null,
            WorkerId: 3,
            ExecutionNumber: 1,
            ActorCode: JobActorCode.Worker,
            ActorKey: null,
            FromStatus: from,
            ToStatus: to,
            ExecutionStatus: null,
            DurationMs: null,
            ReasonCode: reason,
            ReasonMessage: message,
            DetailText: detail
        );

    [Fact]
    public void Events_plain_writes_one_line_per_event_with_reason_detail()
    {
        var w = new StringWriter();
        var page = new PagedResult<JobEventListItem>(
            [
                Event(JobEventCode.JobExecutionStarted, JobStatusCode.Ready, JobStatusCode.Executing),
                Event(
                    JobEventCode.JobExecutionFinished,
                    JobStatusCode.Executing,
                    JobStatusCode.Failed,
                    JobEventReasonCode.JobUnhandledException,
                    "boom in handler"
                ),
            ],
            NextCursor: null,
            HasMore: false,
            PageSize: 50,
            TotalCount: null
        );

        CliOutput.WriteEvents(w, 7, page, json: false);
        var text = w.ToString();

        Assert.Contains("Events for job 7", text);
        Assert.Contains("job.execution-started  ready -> executing", text);
        Assert.Contains("job.execution-finished  executing -> failed", text);
        Assert.Contains("reason: job.unhandled-exception", text);
        Assert.Contains("message: boom in handler", text);
    }

    [Fact]
    public void Events_plain_empty_page_prints_no_events()
    {
        var w = new StringWriter();
        var page = new PagedResult<JobEventListItem>([], NextCursor: null, HasMore: false, PageSize: 50, TotalCount: null);
        CliOutput.WriteEvents(w, 7, page, json: false);
        Assert.Contains("(no events)", w.ToString());
    }

    [Fact]
    public void Events_json_writes_event_array_and_next_cursor()
    {
        var w = new StringWriter();
        var page = new PagedResult<JobEventListItem>(
            [
                Event(
                    JobEventCode.JobExecutionFinished,
                    JobStatusCode.Executing,
                    JobStatusCode.Failed,
                    JobEventReasonCode.JobUnhandledException,
                    "boom"
                ),
            ],
            NextCursor: "next-cur",
            HasMore: true,
            PageSize: 50,
            TotalCount: null
        );

        CliOutput.WriteEvents(w, 7, page, json: true);
        var text = w.ToString();

        Assert.Contains("\"jobId\":7", text);
        Assert.Contains("\"event\":\"job.execution-finished\"", text);
        Assert.Contains("\"reasonCode\":\"job.unhandled-exception\"", text);
        Assert.Contains("\"reasonMessage\":\"boom\"", text);
        Assert.Contains("\"nextCursor\":\"next-cur\"", text);
    }

    [Fact]
    public void Snapshot_failed_hints_at_events_command()
    {
        var w = new StringWriter();
        var s = new JobSnapshot(
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
            JobDefinitionId: 11,
            TenantId: null,
            TenantKey: null,
            Status: JobStatusCode.Failed,
            Priority: JobPriorityCode.Normal,
            ExecutionNumber: 1,
            FailureCount: 3,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ExclusiveKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 11, 8, 0, 0, DateTimeKind.Utc)
        );
        CliOutput.WriteSnapshot(w, s, json: false);
        Assert.Contains("events: run 'jobs events", w.ToString());
    }

    private static JobExplanation Explanation() =>
        new(
            JobId: 4821,
            JobRef: JobRef.New(),
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Suspended,
            StatusMeaning: JobStatusCode.Suspended.Description,
            Headline: "Suspended, waiting for signal \"fraud-review\".",
            ActiveWait: new JobExplainWait(JobExplainWaitKind.Signal, "fraud-review", null),
            Lease: null,
            LastExecutedBy: "payments-v42 (17)",
            Steps: [new JobExplainStep("reserve-stock", JobStepStatusCode.Succeeded, "succeeded and will not rerun")],
            Reason: null,
            NextActions:
            [
                new JobExplainAction("raise-signal", "raise signal \"fraud-review\""),
                new JobExplainAction("cancel", "cancel the job"),
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
        Assert.Contains("- Last executed on worker payments-v42 (17).", text);
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
        var explanation = new JobExplanation(
            JobId: 4821,
            JobRef: JobRef.New(),
            JobNamespace: "payments",
            JobName: "checkout",
            Status: JobStatusCode.Executing,
            StatusMeaning: JobStatusCode.Executing.Description,
            Headline: "Executing, but its lease expired 2m ago.",
            ActiveWait: null,
            Lease: new JobExplainLease(
                WorkerId: 17,
                WorkerName: "payments-v42",
                ExpiresAtUtc: new DateTime(2026, 7, 4, 11, 58, 0, DateTimeKind.Utc),
                Expired: true,
                WorkerLastSeenAtUtc: new DateTime(2026, 7, 4, 11, 56, 0, DateTimeKind.Utc),
                WorkerStale: true,
                RecoveryExpectation: "Recovery should return it to Ready on the next maintenance tick."
            ),
            LastExecutedBy: null,
            Steps: [],
            Reason: "worker shutdown",
            NextActions:
            [
                new JobExplainAction("wait-recovery", "wait for sys.recovery to reclaim the job on the next maintenance tick"),
                new JobExplainAction("cancel", "cancel the job if it should not continue"),
            ]
        );

        CliOutput.WriteExplanation(w, explanation, json: false);
        var text = w.ToString();

        Assert.Contains("Reason:", text);
        Assert.Contains("- Worker shutdown.", text);
        Assert.Contains("Worker:", text);
        Assert.Contains("- Worker payments-v42 (17), lease expired at 2026-07-04T11:58:00.0000000Z.", text);
        Assert.Contains("- Last heartbeat at 2026-07-04T11:56:00.0000000Z.", text);
        Assert.Contains("- Worker is marked Dead.", text);
        Assert.Contains("Next actions:", text);
        Assert.Contains("- Wait for sys.recovery to reclaim the job on the next maintenance tick.", text);
        Assert.Contains("- Cancel the job if it should not continue.", text);
    }

    [Fact]
    public void Explanation_plain_writes_timer_wait_as_executor_free_durable_work()
    {
        var w = new StringWriter();
        var explanation = new JobExplanation(
            JobId: 4821,
            JobRef: JobRef.New(),
            JobNamespace: "emails",
            JobName: "send-follow-up",
            Status: JobStatusCode.Suspended,
            StatusMeaning: JobStatusCode.Suspended.Description,
            Headline: "Suspended on durable sleep \"cool-down\", due in 12m.",
            ActiveWait: new JobExplainWait(JobExplainWaitKind.Timer, "cool-down", new DateTime(2026, 7, 4, 12, 12, 0, DateTimeKind.Utc)),
            Lease: null,
            LastExecutedBy: null,
            Steps: [],
            Reason: null,
            NextActions: [new JobExplainAction("none", "no action needed - it resumes when the timer is due")]
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

        Assert.Contains("\"jobId\":4821", text);
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
        CliOutput.WriteDebugRun(plain, 5, "Completed", JobStatusCode.Succeeded, json: false);
        Assert.Contains("run: Completed", plain.ToString());
        Assert.Contains("status: Succeeded", plain.ToString());

        var json = new StringWriter();
        CliOutput.WriteDebugRun(json, 5, "Rearmed", null, json: true);
        Assert.Contains("\"run\":\"Rearmed\"", json.ToString());
        Assert.Contains("\"status\":null", json.ToString());
    }
}
