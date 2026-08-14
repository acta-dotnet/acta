using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for the control-verb state matrix: per-status legality guards, failure_count reset on
/// Restart, retention stamping and re-cancel rejection on Cancel, re-pause idempotence on Pause, and
/// the explicit vs DB-now next-run coalescing on Resume.
/// </summary>
[ConformanceSpec(
    "control.state-matrix",
    "Control verbs apply per-status guards and correct side effects",
    Area = "Control",
    Contract = "Restart revives Failed or Cancelled resetting failure_count, Cancel stamps retention and rejects re-cancel, Pause allows re-pause, Resume coalesces next run.",
    Arrange = "Enqueued jobs are placed into each source status via raw SQL UPDATE.",
    Act = "Pause, Resume, Cancel, and Restart are invoked against jobs in each source status.",
    Assert = "Each verb returns the expected outcome and status and applies its side effects such as failure_count reset and retention stamping."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PauseJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ResumeJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RestartJobAsync))]
public abstract class ControlVerbStateMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
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

        var outcomeFromFailed = await Services.GetRequiredService<IJobStore>().RestartJobAsync(failedJob, input, null, ct);
        var outcomeFromCancelled = await Services.GetRequiredService<IJobStore>().RestartJobAsync(cancelledJob, input, null, ct);

        Assert.Equal(JobControlActionInternal.Applied, outcomeFromFailed.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeFromFailed.Status);
        Assert.Equal(JobControlActionInternal.Applied, outcomeFromCancelled.Action);
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

        var outcome = await Services.GetRequiredService<IJobStore>().RestartJobAsync(jobId, input, null, ct);

        Assert.Equal(JobControlActionInternal.Rejected, outcome.Action);
        Assert.Equal(JobStatusCode.Executing, outcome.Status);

        var state = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Executing, state.Status);
    }

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
        var tBefore = DateTime.UtcNow.AddSeconds(-1);
        var cancelSuspended = await Services.GetRequiredService<IJobStore>().CancelJobAsync(suspendedJob, input, ct);
        var cancelDispatched = await Services.GetRequiredService<IJobStore>().CancelJobAsync(dispatchedJob, input, ct);
        var tAfter = DateTime.UtcNow.AddDays(91);

        Assert.Equal(JobControlActionInternal.Applied, cancelSuspended.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancelSuspended.Outcome.Status);
        Assert.Equal(JobControlActionInternal.Applied, cancelDispatched.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancelDispatched.Outcome.Status);

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

        var first = await Services.GetRequiredService<IJobStore>().CancelJobAsync(jobId, input, ct);
        Assert.Equal(JobControlActionInternal.Applied, first.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, first.Outcome.Status);

        var afterFirst = await ReadJobAsync(jobId, ct);
        var retentionAfterFirst = afterFirst.RetentionUntilUtc;

        var second = await Services.GetRequiredService<IJobStore>().CancelJobAsync(jobId, input, ct);
        Assert.Equal(JobControlActionInternal.Rejected, second.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, second.Outcome.Status);

        var afterSecond = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, afterSecond.Status);
        Assert.Equal(retentionAfterFirst, afterSecond.RetentionUntilUtc);
    }

    [Fact(DisplayName = "Pause from Suspended is Applied and re-pause from Paused is also Applied")]
    public async Task Pause_from_Suspended_and_repause_from_Paused_both_Applied()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("pause test");

        var jobId = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, jobId, (byte)JobStatusCode.Suspended, ct);

        var firstPause = await Services.GetRequiredService<IJobStore>().PauseJobAsync(jobId, input, ct);
        Assert.Equal(JobControlActionInternal.Applied, firstPause.Action);
        Assert.Equal(JobStatusCode.Paused, firstPause.Status);

        var afterFirst = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Paused, afterFirst.Status);

        var secondPause = await Services.GetRequiredService<IJobStore>().PauseJobAsync(jobId, input, ct);
        Assert.Equal(JobControlActionInternal.Applied, secondPause.Action);
        Assert.Equal(JobStatusCode.Paused, secondPause.Status);

        var afterSecond = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Paused, afterSecond.Status);
    }

    [Fact(DisplayName = "Resume with explicit next_run_at_utc pins the instant; null coalesces to DB-now")]
    public async Task Resume_explicit_next_run_pins_instant_null_coalesces_to_db_now()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("resume next-run branch");

        var explicitJob = await EnqueueAsync(ct);
        var defaultJob = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, explicitJob, (byte)JobStatusCode.Paused, ct);
        await SetJobStatusAsync(Db, defaultJob, (byte)JobStatusCode.Paused, ct);

        // Far-future instant with no sub-second component so the round-trip is lossless.
        var explicitNextRun = new DateTime(2027, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var outcomeExplicit = await Services.GetRequiredService<IJobStore>().ResumeJobAsync(explicitJob, input, explicitNextRun, ct);

        var tBefore = DateTime.UtcNow.AddSeconds(-1);
        var outcomeDefault = await Services.GetRequiredService<IJobStore>().ResumeJobAsync(defaultJob, input, null, ct);
        var tAfter = DateTime.UtcNow.AddSeconds(1);

        Assert.Equal(JobControlActionInternal.Applied, outcomeExplicit.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeExplicit.Status);
        Assert.Equal(JobControlActionInternal.Applied, outcomeDefault.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeDefault.Status);

        var explicitState = await ReadJobAsync(explicitJob, ct);
        Assert.Equal(JobStatusCode.Ready, explicitState.Status);
        Assert.Equal(explicitNextRun, explicitState.NextRunAtUtc);

        var defaultState = await ReadJobAsync(defaultJob, ct);
        Assert.Equal(JobStatusCode.Ready, defaultState.Status);
        Assert.NotNull(defaultState.NextRunAtUtc);
        Assert.InRange(defaultState.NextRunAtUtc!.Value, tBefore, tAfter);
    }

    // ---------- helpers ----------

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static JobControlInput ControlInput(string reason) =>
        new(new JobControlActor(ActorCode.Operator, "test"), JobEventReasonCode.JobControlManual, reason);

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status WHERE job_id = @p_id",
            ct,
            ("@p_status", statusCode),
            ("@p_id", jobId)
        );

    private static Task SetFailureCountAsync(IDbSession db, long jobId, short count, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET failure_count = @p_count WHERE job_id = @p_id",
            ct,
            ("@p_count", count),
            ("@p_id", jobId)
        );
}
