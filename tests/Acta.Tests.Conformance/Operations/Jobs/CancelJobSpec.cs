using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for cancel's status guard: a live job cancels with a retention stamp, a cancelled one rejects.</summary>
[ConformanceSpec(
    "cancel-job.state-guard",
    "Cancel stamps retention once and rejects a re-cancel",
    Area = "Control",
    Contract = "Cancel from Suspended or Dispatched applies and stamps retention_until_utc, and re-cancelling a Cancelled job is Rejected without re-stamping."
)]
public sealed class CancelJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The database clock, which is the one the verbs stamp their instants from.
    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
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

    [Fact(DisplayName = "Cancel from Suspended and Dispatched is Applied and stamps retention_until_utc")]
    public async Task Cancel_from_Suspended_and_Dispatched_is_Applied_and_stamps_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("cancel non-terminal");

        var suspendedJob = await EnqueueAsync(ct);
        var dispatchedJob = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, suspendedJob, (byte)JobStatusCode.Suspended, ct);
        await SetJobStatusAsync(Db, dispatchedJob, (byte)JobStatusCode.Dispatched, ct);

        // Default retention is 90 days so retention_until_utc will be ~now + 90d; capture a window.
        // Both ends come off the DATABASE clock, because the value being bracketed does: comparing a
        // db_now-derived instant against this process's clock would fold container clock skew into a
        // one-second lower bound, and skew is not what this fact is about.
        var tBefore = (await NowAsync(ct)).AddSeconds(-1);
        var cancelSuspended = await Services.GetRequiredService<CancelJobHandler>().CancelAsync(suspendedJob, input, ct);
        var cancelDispatched = await Services.GetRequiredService<CancelJobHandler>().CancelAsync(dispatchedJob, input, ct);
        var tAfter = (await NowAsync(ct)).AddDays(91);

        Assert.Equal(ControlAction.Applied, cancelSuspended.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancelSuspended.Status);
        Assert.Equal(ControlAction.Applied, cancelDispatched.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancelDispatched.Status);

        var suspendedState = await ReadJobAsync(suspendedJob, ct);
        Assert.Equal(JobStatusCode.Cancelled, suspendedState.Status);
        Assert.NotNull(suspendedState.RetentionUntilUtc);
        Assert.InRange(suspendedState.RetentionUntilUtc!.Value, tBefore, tAfter);

        var dispatchedState = await ReadJobAsync(dispatchedJob, ct);
        Assert.Equal(JobStatusCode.Cancelled, dispatchedState.Status);
        Assert.NotNull(dispatchedState.RetentionUntilUtc);
        Assert.InRange(dispatchedState.RetentionUntilUtc!.Value, tBefore, tAfter);
    }

    [Fact(DisplayName = "Re-cancel of a Cancelled job is Rejected and does not re-stamp retention_until_utc")]
    public async Task Recancel_is_Rejected_and_leaves_retention_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("cancel then re-cancel");

        var jobId = await EnqueueAsync(ct);

        var first = await Services.GetRequiredService<CancelJobHandler>().CancelAsync(jobId, input, ct);
        Assert.Equal(ControlAction.Applied, first.Action);
        Assert.Equal(JobStatusCode.Cancelled, first.Status);

        var afterFirst = await ReadJobAsync(jobId, ct);
        var retentionAfterFirst = afterFirst.RetentionUntilUtc;

        var second = await Services.GetRequiredService<CancelJobHandler>().CancelAsync(jobId, input, ct);
        Assert.Equal(ControlAction.Rejected, second.Action);
        Assert.Equal(JobStatusCode.Cancelled, second.Status);

        var afterSecond = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, afterSecond.Status);
        Assert.Equal(retentionAfterFirst, afterSecond.RetentionUntilUtc);
    }
}
