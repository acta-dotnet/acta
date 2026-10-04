using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for restart's status guard: Failed and Cancelled revive with a clean failure count, Executing is rejected.</summary>
[ConformanceSpec(
    "restart-job.state-guard",
    "Restart revives Failed or Cancelled jobs and rejects an executing one",
    Area = "Control",
    Contract = "Restart revives a Failed or Cancelled job with failure_count reset and retention cleared, and is Rejected from Executing."
)]
public sealed class RestartJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The database clock, which is the one the verbs stamp their instants from.
    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static JobControlInput ControlInput(string reason) =>
        new(new JobControlActor(ActorCode.Operator, "test"), EventReasonCode.JobControlManual, reason);

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        RuntimeStateStaging.SetStatusAsync(db, jobId, statusCode, ct);

    private static Task SetFailureCountAsync(IDbSession db, long jobId, short count, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET failure_count = @p_count WHERE job_id = @p_id",
            ct,
            ("@p_count", count),
            ("@p_id", jobId)
        );

    [Fact(DisplayName = "Restart revives Failed and Cancelled jobs resetting failure_count to 0 and clearing retention")]
    public async Task Restart_revives_Failed_and_Cancelled_resetting_failure_count_and_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("revive from terminal");

        var failedJob = await EnqueueAsync(ct);
        var cancelledJob = await EnqueueAsync(ct);

        await SetJobStatusAsync(Db, failedJob, (byte)JobStatusCode.Failed, ct);
        await SetFailureCountAsync(Db, failedJob, 3, ct);
        await SetJobStatusAsync(Db, cancelledJob, (byte)JobStatusCode.Cancelled, ct);

        var outcomeFromFailed = await Services.GetRequiredService<RestartJobHandler>().RestartAsync(failedJob, input, null, ct);
        var outcomeFromCancelled = await Services.GetRequiredService<RestartJobHandler>().RestartAsync(cancelledJob, input, null, ct);

        Assert.Equal(ControlAction.Applied, outcomeFromFailed.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeFromFailed.Status);
        Assert.Equal(ControlAction.Applied, outcomeFromCancelled.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeFromCancelled.Status);

        var failedState = await ReadJobAsync(failedJob, ct);
        Assert.Equal(JobStatusCode.Ready, failedState.Status);
        Assert.Equal(0, failedState.FailureCount);
        Assert.Null(failedState.RetentionUntilUtc);

        var cancelledState = await ReadJobAsync(cancelledJob, ct);
        Assert.Equal(JobStatusCode.Ready, cancelledState.Status);
        Assert.Equal(0, cancelledState.FailureCount);
        Assert.Null(cancelledState.RetentionUntilUtc);
    }

    [Fact(DisplayName = "Restart from Executing is Rejected and leaves the status unchanged")]
    public async Task Restart_from_Executing_is_Rejected_and_leaves_status_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("restart while executing");

        var jobId = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, jobId, (byte)JobStatusCode.Executing, ct);

        var outcome = await Services.GetRequiredService<RestartJobHandler>().RestartAsync(jobId, input, null, ct);

        Assert.Equal(ControlAction.Rejected, outcome.Action);
        Assert.Equal(JobStatusCode.Executing, outcome.Status);

        var state = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Executing, state.Status);
    }
}
