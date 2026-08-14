using Acta.Runtime.Modules.Execution.Jobs;
using Xunit;

namespace Acta.Tests.Jobs;

public class JobExplainerTests
{
    private static readonly DateTime Now = new(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ExplainHeaderRow Header(
        JobStatusCode status,
        int executionNumber = 0,
        short failureCount = 0,
        short maxAttempts = 3,
        DateTime? nextRunAtUtc = null,
        int? leasedByWorkerId = null,
        DateTime? leaseExpiresAtUtc = null,
        string? workerDeploymentVersion = null,
        WorkerStatusCode? workerStatus = null,
        DateTime? workerLastSeenAtUtc = null,
        JobEventReasonCode? latestReasonCode = null,
        string? latestReasonMessage = null,
        int? lastExecutedByWorkerId = null,
        string? lastExecutedByWorkerName = null
    ) =>
        new(
            JobId: 4821,
            JobRef: Guid.NewGuid(),
            JobNamespace: "payments",
            JobName: "checkout",
            Status: status,
            ExecutionNumber: executionNumber,
            FailureCount: failureCount,
            MaxAttemptsEffective: maxAttempts,
            NextRunAtUtc: nextRunAtUtc,
            LeasedByWorkerId: leasedByWorkerId,
            LeaseExpiresAtUtc: leaseExpiresAtUtc,
            WorkerDeploymentVersion: workerDeploymentVersion,
            WorkerStatus: workerStatus,
            WorkerLastHeartbeatAtUtc: workerLastSeenAtUtc,
            LatestReasonCode: latestReasonCode,
            LatestReasonMessage: latestReasonMessage,
            LastExecutedByWorkerId: lastExecutedByWorkerId,
            LastExecutedByWorkerName: lastExecutedByWorkerName
        );

    private static JobExplainData Data(
        ExplainHeaderRow header,
        IReadOnlyList<ExplainStepRow>? steps = null,
        IReadOnlyList<ExplainCheckpointRow>? checkpoints = null
    ) => new(header, steps ?? [], checkpoints ?? []);

    private static ExplainCheckpointRow Checkpoint(
        JobCheckpointKindCode kind,
        string name,
        JobCheckpointStatusCode? state,
        DateTime? dueAtUtc = null
    ) => new(kind, name, state, dueAtUtc);

    [Fact]
    public void Suspended_on_signal_names_the_signal_and_offers_raise_or_cancel()
    {
        var data = Data(
            Header(JobStatusCode.Suspended),
            checkpoints: [Checkpoint(JobCheckpointKindCode.Signal, "fraud-review", JobCheckpointStatusCode.Pending)]
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.NotNull(x.ActiveWait);
        Assert.Equal(JobExplainWaitKind.Signal, x.ActiveWait!.Kind);
        Assert.Equal("fraud-review", x.ActiveWait.Name);
        Assert.Contains("waiting for signal \"fraud-review\"", x.Headline);
        Assert.Contains(x.NextActions, a => a.Kind == "raise-signal" && a.Description.Contains("fraud-review"));
        Assert.Contains(x.NextActions, a => a.Kind == "cancel");
        Assert.Null(x.Lease);
    }

    [Fact]
    public void Suspended_on_timer_reports_due_and_does_not_prescribe_a_signal()
    {
        var data = Data(
            Header(JobStatusCode.Suspended),
            checkpoints: [Checkpoint(JobCheckpointKindCode.Timer, "acta.sleep", JobCheckpointStatusCode.Pending, Now.AddMinutes(30))]
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.Equal(JobExplainWaitKind.Timer, x.ActiveWait!.Kind);
        Assert.Equal(Now.AddMinutes(30), x.ActiveWait.DueAtUtc);
        Assert.Contains("durable sleep \"acta.sleep\"", x.Headline);
        Assert.Contains("due in 30m", x.Headline);
        Assert.DoesNotContain(x.NextActions, a => a.Kind == "raise-signal");
    }

    [Fact]
    public void Suspended_with_missing_wait_details_points_to_the_timeline()
    {
        var x = JobExplainer.Explain(Data(Header(JobStatusCode.Suspended)), Now);

        Assert.Null(x.ActiveWait);
        Assert.Contains("no pending signal or timer checkpoint", x.Headline);
        Assert.Contains(x.NextActions, a => a.Kind == "inspect-timeline");
        Assert.Contains(x.NextActions, a => a.Kind == "cancel");
    }

    [Fact]
    public void Executing_with_a_live_lease_names_the_worker_and_needs_no_action()
    {
        var data = Data(
            Header(
                JobStatusCode.Executing,
                leasedByWorkerId: 17,
                leaseExpiresAtUtc: Now.AddMinutes(2),
                workerDeploymentVersion: "payments-v42",
                workerStatus: WorkerStatusCode.Active,
                workerLastSeenAtUtc: Now.AddSeconds(-12)
            )
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.NotNull(x.Lease);
        Assert.False(x.Lease!.Expired);
        Assert.Equal(17, x.Lease.WorkerId);
        Assert.Equal("payments-v42", x.Lease.WorkerName);
        Assert.Contains("Executing on worker payments-v42 (17)", x.Headline);
        Assert.Contains("Lease expires in 2m", x.Headline);
        Assert.Contains(x.NextActions, a => a.Kind == "none");
    }

    [Fact]
    public void Executing_with_an_expired_lease_reports_the_lapse_and_expects_recovery_to_ready()
    {
        var data = Data(
            Header(
                JobStatusCode.Executing,
                failureCount: 0,
                maxAttempts: 3,
                leasedByWorkerId: 17,
                leaseExpiresAtUtc: Now.AddMinutes(-2),
                workerDeploymentVersion: "payments-v42",
                workerStatus: WorkerStatusCode.Active,
                workerLastSeenAtUtc: Now.AddMinutes(-4)
            )
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.True(x.Lease!.Expired);
        Assert.Contains("lease expired 2m ago", x.Headline);
        Assert.Contains("Ready", x.Lease.RecoveryExpectation);
        Assert.Contains(x.NextActions, a => a.Kind == "wait-recovery");
        Assert.DoesNotContain(x.NextActions, a => a.Kind == "restart");
        Assert.False(x.Lease.WorkerStale);
    }

    [Fact]
    public void Expired_lease_past_the_retry_budget_expects_recovery_to_fail_it()
    {
        var data = Data(
            Header(
                JobStatusCode.Executing,
                failureCount: 2,
                maxAttempts: 3,
                leasedByWorkerId: 17,
                leaseExpiresAtUtc: Now.AddMinutes(-1),
                workerStatus: WorkerStatusCode.Dead,
                workerLastSeenAtUtc: Now.AddMinutes(-6)
            )
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.Contains("Failed", x.Lease!.RecoveryExpectation);
        Assert.True(x.Lease.WorkerStale);
    }

    [Fact]
    public void Failed_surfaces_the_latest_reason_and_offers_timeline_and_restart()
    {
        var data = Data(Header(JobStatusCode.Failed, latestReasonMessage: "boom in handler"));

        var x = JobExplainer.Explain(data, Now);

        Assert.Equal("boom in handler", x.Reason);
        Assert.Equal("Failed.", x.Headline);
        Assert.Contains(x.NextActions, a => a.Kind == "inspect-timeline");
        Assert.Contains(x.NextActions, a => a.Kind == "restart" && a.Description.Contains("underlying cause"));
    }

    [Fact]
    public void Ready_in_the_future_is_scheduled_and_ready_now_waits_for_a_worker()
    {
        var scheduled = JobExplainer.Explain(Data(Header(JobStatusCode.Ready, nextRunAtUtc: Now.AddMinutes(5))), Now);
        Assert.Contains("scheduled to run in 5m", scheduled.Headline);
        Assert.Contains(scheduled.NextActions, a => a.Description.Contains("next run time"));

        var idle = JobExplainer.Explain(Data(Header(JobStatusCode.Ready, nextRunAtUtc: Now.AddMinutes(-1))), Now);
        Assert.Equal("Ready and eligible for claim.", idle.Headline);
        Assert.Contains(idle.NextActions, a => a.Description.Contains("namespace \"payments\""));
    }

    [Fact]
    public void Suspended_names_the_worker_that_last_ran_it()
    {
        var data = Data(
            Header(JobStatusCode.Suspended, lastExecutedByWorkerId: 17, lastExecutedByWorkerName: "payments-v42"),
            checkpoints: [Checkpoint(JobCheckpointKindCode.Signal, "fraud-review", JobCheckpointStatusCode.Pending)]
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.Equal("payments-v42 (17)", x.LastExecutedBy);
    }

    [Fact]
    public void Paused_offers_resume()
    {
        var x = JobExplainer.Explain(Data(Header(JobStatusCode.Paused)), Now);
        Assert.Contains("Paused", x.Headline);
        Assert.Contains(x.NextActions, a => a.Kind == "resume");
    }

    [Fact]
    public void Steps_render_succeeded_pending_and_exhausted_phrases()
    {
        var data = Data(
            Header(JobStatusCode.Suspended),
            steps:
            [
                new ExplainStepRow(
                    "reserve-stock",
                    JobStepStatusCode.Succeeded,
                    AttemptNumber: 1,
                    NextRetryAtUtc: null,
                    ReasonMessage: null
                ),
                new ExplainStepRow(
                    "charge-card",
                    JobStepStatusCode.Pending,
                    AttemptNumber: 2,
                    NextRetryAtUtc: Now.AddMinutes(1),
                    ReasonMessage: "declined"
                ),
                new ExplainStepRow(
                    "ship",
                    JobStepStatusCode.Exhausted,
                    AttemptNumber: 5,
                    NextRetryAtUtc: null,
                    ReasonMessage: "carrier down"
                ),
            ]
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.Equal(3, x.Steps.Count);
        Assert.Contains("succeeded and will not rerun", x.Steps[0].Explanation);
        Assert.Contains("waiting for retry attempt 2: declined", x.Steps[1].Explanation);
        Assert.Contains("exhausted after 5 attempts: carrier down", x.Steps[2].Explanation);
    }

    [Fact]
    public void Done_reports_success_and_keeps_durable_step_facts()
    {
        var data = Data(
            Header(JobStatusCode.Succeeded),
            steps: [new ExplainStepRow("reserve-stock", JobStepStatusCode.Succeeded, 1, null, null)]
        );

        var x = JobExplainer.Explain(data, Now);

        Assert.Equal("Succeeded.", x.Headline);
        var step = Assert.Single(x.Steps);
        Assert.Equal("reserve-stock", step.Name);
        Assert.Contains("will not rerun", step.Explanation);
        Assert.Contains(x.NextActions, a => a.Kind == "view-result");
    }

    [Fact]
    public void Cancelled_surfaces_reason_separately_and_only_restarts_if_reversed()
    {
        var x = JobExplainer.Explain(Data(Header(JobStatusCode.Cancelled, latestReasonMessage: "superseded")), Now);

        Assert.Equal("Cancelled.", x.Headline);
        Assert.Equal("superseded", x.Reason);
        Assert.Contains(x.NextActions, a => a.Kind == "restart" && a.Description.Contains("cancellation should be reversed"));
    }

    [Fact]
    public void Status_meaning_decodes_the_code_description()
    {
        var x = JobExplainer.Explain(Data(Header(JobStatusCode.Executing, leasedByWorkerId: 1, leaseExpiresAtUtc: Now.AddMinutes(1))), Now);
        Assert.Equal(JobStatusCode.Executing.Description, x.StatusMeaning);
    }
}
