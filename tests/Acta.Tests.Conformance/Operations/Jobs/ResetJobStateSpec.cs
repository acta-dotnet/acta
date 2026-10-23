using System.Globalization;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>reset_job_state</c> (<c>ctx.ResetStateAsync</c>): clears a Job's checkpoints /
/// steps rows so the next execution starts as new,
/// leaving the job row and sibling jobs untouched, and emits one audit-filtered <c>job.state-reset</c>
/// substrate event (no status transition).
/// </summary>
[ConformanceSpec(
    "reset-job-state.clears-substrate",
    "Reset clears one job's substrate and emits an audit-gated state-reset event",
    Area = "Execution",
    Contract = "Reset clears a job's substrate rows leaving siblings intact and emits one audit-gated state-reset event with no status transition."
)]
public sealed class ResetJobStateSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Target job substrate is cleared while sibling jobs are left untouched")]
    public async Task Reset_clears_all_substrate_for_the_job_and_leaves_siblings_intact()
    {
        var ct = TestContext.Current.CancellationToken;

        var target = await EnqueueAsync(ct);
        var sibling = await EnqueueAsync(ct);
        await SeedSubstrateAsync(Db, target, ct);
        await SeedSubstrateAsync(Db, sibling, ct);

        await Services.GetRequiredService<ResetJobStateHandler>().HandleAsync(new ResetJobStateCommand(target), ct);

        await AssertSubstrateCountsAsync(Db, target, 0, ct);
        await AssertSubstrateCountsAsync(Db, sibling, 1, ct);
    }

    [Fact(DisplayName = "One JobStateReset event is emitted with the job as its actor and no reason")]
    public async Task Reset_emits_one_state_reset_event_with_job_actor()
    {
        var ct = TestContext.Current.CancellationToken;

        var jobId = await EnqueueAsync(ct);
        await SeedSubstrateAsync(Db, jobId, ct);

        await Services.GetRequiredService<ResetJobStateHandler>().HandleAsync(new ResetJobStateCommand(jobId), ct);

        var ev = await ReadSingleEventAsync(jobId, EventCode.JobStateReset, ct);
        Assert.Equal(jobId.ToString(CultureInfo.InvariantCulture), EventDetail(ev, "actor"));
        Assert.Null(EventDetail(ev, "reason"));
        Assert.Equal(0, ev.WorkerId);
    }

    [Fact(DisplayName = "Reset below Audit recording clears the rows but emits no event")]
    public async Task Reset_below_audit_recording_clears_rows_but_emits_no_event()
    {
        var ct = TestContext.Current.CancellationToken;

        var jobId = await EnqueueAsync(ct);
        await SeedSubstrateAsync(Db, jobId, ct);
        await SetEventRecordingAsync(Db, jobId, eventRecording: 0, ct);

        await Services.GetRequiredService<ResetJobStateHandler>().HandleAsync(new ResetJobStateCommand(jobId), ct);

        await AssertSubstrateCountsAsync(Db, jobId, 0, ct);

        Assert.Equal(0, await CountEventsAsync(jobId, EventCode.JobStateReset, ct));
    }

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    // Populates every substrate table for one job: variable/timer/signal via their operations,
    // step via a direct insert (no op surface creates one mid-handler).
    private async Task SeedSubstrateAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var execution = Services.GetRequiredService<ExecutionOperations>();
        await CheckpointSlot.SetAsync(execution, jobId, CheckpointKindCode.Variable, "v.test", JobPayload.Text("x"), ct);
        await execution.ArmOrConsumeSleepTimerAsync(new ArmOrConsumeSleepTimerCommand(jobId, "t.test", 60, null), ct);
        await Services
            .GetRequiredService<SignalsOperations>()
            .RaiseSignalAsync(
                new RaiseSignalSlot(jobId, CheckpointKindCode.Signal, "s.test", 0, null, new JobControlInput("op", null, "seed")),
                ct
            );

        await db.ExecuteRawAsync(
            "INSERT INTO {schema}.steps (job_id, step_name, step_status_code, attempt_number, result_format_id)"
                + " VALUES (@p_job_id, @p_name, @p_state, 1, 0)",
            ct,
            ("@p_job_id", jobId),
            ("@p_name", "a.test"),
            ("@p_state", (byte)StepStatusCode.Succeeded)
        );
    }

    private static async Task AssertSubstrateCountsAsync(IDbSession db, long jobId, int expected, CancellationToken ct)
    {
        // One checkpoint per seeded kind (variable, timer, signal); expected scales per kind.
        Assert.Equal(
            expected,
            await db.From<CheckpointEntity>().Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Variable).CountAsync(ct)
        );
        Assert.Equal(
            expected,
            await db.From<CheckpointEntity>().Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Timer).CountAsync(ct)
        );
        Assert.Equal(
            expected,
            await db.From<CheckpointEntity>().Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal).CountAsync(ct)
        );
        Assert.Equal(expected, await db.From<StepEntity>().Where(a => a.JobId == jobId).CountAsync(ct));
    }

    private static Task SetEventRecordingAsync(IDbSession db, long jobId, byte eventRecording, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET event_recording_code = @p_level WHERE job_id = @p_id",
            ct,
            ("@p_level", eventRecording),
            ("@p_id", jobId)
        );
}
