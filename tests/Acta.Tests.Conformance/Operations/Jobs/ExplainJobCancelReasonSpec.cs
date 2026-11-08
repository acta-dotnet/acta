using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for a cancelled job's explanation: its reason is the cancel's, read by the explain statement's reason lookup
/// from the job.cancelled event, which only the Audit recording level writes.
/// </summary>
[ConformanceSpec(
    "explain-job.cancel-reason",
    "ExplainJob gives a cancelled job its cancel's reason",
    Area = "Reads",
    Contract = "ExplainJob's reason for a Cancelled job is the job.cancelled event's message and actor, and null where no such event was recorded."
)]
public sealed class ExplainJobCancelReasonSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
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

    [Fact(DisplayName = "At the failures-only recording level a cancelled job has no reason, not the failure before the cancel")]
    public async Task A_cancel_recorded_nowhere_leaves_no_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 6);
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(job, "duplicate order", "alice", ct: ct)).Action);
        var explanation = Assert.IsType<ExplainJobResponse>(await Jobs.ExplainAsync(job, ct));

        Assert.Equal(JobStatusCode.Cancelled, explanation.Status);
        Assert.Null(explanation.Reason);
    }
}
