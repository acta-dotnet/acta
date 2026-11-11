using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for a cancelled job's explanation: its reason is the cancel's, read by the explain statement's reason lookup
/// from the job.cancelled event, which a caller's own cancel writes at every recording level and a cascaded one only at Audit.
/// </summary>
[ConformanceSpec(
    "explain-job.cancel-reason",
    "ExplainJob gives a cancelled job its cancel's reason",
    Area = "Reads",
    Contract = "ExplainJob's reason for a Cancelled job is the job.cancelled event's message and actor, and null where no such event was recorded."
)]
[Deploys("add-numbers", "failures-audit-probe")]
public sealed class ExplainJobCancelReasonSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ExplainJobCancelReasonSpec>>
{
    [Fact(DisplayName = "A cancelled job's reason is the cancel's message, followed by the operator who cancelled it")]
    public async Task A_cancelled_job_explains_its_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(job, "duplicate order", "alice", ct: ct)).Action);
        var explanation = Assert.IsType<ExplainJobResponse>(await Jobs.ExplainAsync(job, ct));

        Assert.Equal(JobStatusCode.Cancelled, explanation.Status);
        Assert.Equal("duplicate order (by alice)", explanation.Reason);
    }

    [Fact(DisplayName = "At the Failures recording level a cancelled job still explains its cancel, not the failure before it")]
    public async Task A_cancel_below_audit_is_explained()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 6);
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(job, "duplicate order", "alice", ct: ct)).Action);
        var explanation = Assert.IsType<ExplainJobResponse>(await Jobs.ExplainAsync(job, ct));

        Assert.Equal(JobStatusCode.Cancelled, explanation.Status);
        Assert.Equal("duplicate order (by alice)", explanation.Reason);
    }

    [Fact(DisplayName = "A cancel before a restart does not explain a later cancel that recorded nothing")]
    public async Task An_earlier_cancel_does_not_explain_a_later_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        var child = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None, ParentJobId: parent.JobId),
            ct
        );
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(child, "bad data", "alice", ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(child, ct: ct)).Action);

        // The parent's cancel reaches the child without a record of its own at this level.
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(parent, "duplicate order", "bob", ct: ct)).Action);
        var explanation = Assert.IsType<ExplainJobResponse>(await Jobs.ExplainAsync(child, ct));

        Assert.Equal(JobStatusCode.Cancelled, explanation.Status);
        Assert.Null(explanation.Reason);
    }

    [Fact(DisplayName = "A job its ancestor's cancel reached below Audit has no reason")]
    public async Task A_cascaded_cancel_below_audit_leaves_no_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        var child = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None, ParentJobId: parent.JobId),
            ct
        );

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(parent, "duplicate order", "alice", ct: ct)).Action);
        var explanation = Assert.IsType<ExplainJobResponse>(await Jobs.ExplainAsync(child, ct));

        Assert.Equal(JobStatusCode.Cancelled, explanation.Status);
        Assert.Null(explanation.Reason);
    }
}
