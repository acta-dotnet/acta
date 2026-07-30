using System.Globalization;
using System.Text;
using Acta.Modules.Execution;
using Acta.Modules.Execution.Checkpoints;
using Acta.Modules.Execution.Jobs;
using Acta.Modules.Execution.Signals;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for <c>reset_job_state</c> (<c>ctx.ResetStateAsync</c>): clears a Job's checkpoints /
/// steps / results rows so the next execution starts as new,
/// leaving the job row and sibling jobs untouched, and emits one audit-filtered <c>job.state-reset</c>
/// substrate event (no status transition).
/// </summary>
[ConformanceSpec(
    "reset-job-state.clears-substrate",
    "Reset clears one job's substrate and emits an audit-gated state-reset event",
    Area = "Execution",
    Contract = "Reset clears a job's substrate rows leaving siblings intact and emits one audit-gated state-reset event with no status transition.",
    Arrange = "Two jobs are seeded with checkpoint, step, and result substrate rows.",
    Act = "ResetJobState targets one job, repeated with audit on and audit off.",
    Assert = "Only the target job's substrate clears with no status transition, and one state-reset event is emitted only when audit is on."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ResetJobStateAsync))]
public abstract class ResetJobStateSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Target job substrate is cleared while sibling jobs are left untouched")]
    public async Task Reset_clears_all_substrate_for_the_job_and_leaves_siblings_intact()
    {
        var ct = TestContext.Current.CancellationToken;

        var target = await EnqueueAsync(ct);
        var sibling = await EnqueueAsync(ct);
        await SeedSubstrateAsync(Db, target, ct);
        await SeedSubstrateAsync(Db, sibling, ct);

        await Services.GetRequiredService<IJobStore>().ResetJobStateAsync(target, ct);

        await AssertSubstrateCountsAsync(Db, target, 0, ct);
        await AssertSubstrateCountsAsync(Db, sibling, 1, ct);
    }

    [Fact(DisplayName = "One JobStateReset event is emitted with the Job actor and no status transition")]
    public async Task Reset_emits_one_state_reset_event_with_job_actor_and_no_status_transition()
    {
        var ct = TestContext.Current.CancellationToken;

        var jobId = await EnqueueAsync(ct);
        await SeedSubstrateAsync(Db, jobId, ct);

        await Services.GetRequiredService<IJobStore>().ResetJobStateAsync(jobId, ct);

        var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == JobEventCode.JobStateReset).ToListAsync(ct);

        var ev = Assert.Single(events);
        Assert.Equal(JobActorCode.Job, ev.ActorCode);
        Assert.Equal(jobId.ToString(CultureInfo.InvariantCulture), ev.ActorKey);
        // Substrate event: no transition, so from == to == the job's current status.
        Assert.Equal(ev.FromStatus, ev.ToStatus);
        Assert.Null(ev.ReasonCode);
        Assert.Null(ev.WorkerId);
    }

    [Fact(DisplayName = "Reset below the audit level clears the rows but emits no event")]
    public async Task Reset_below_audit_level_clears_rows_but_emits_no_event()
    {
        var ct = TestContext.Current.CancellationToken;

        var jobId = await EnqueueAsync(ct);
        await SeedSubstrateAsync(Db, jobId, ct);
        await SetAuditLevelAsync(Db, jobId, auditLevel: 0, ct);

        await Services.GetRequiredService<IJobStore>().ResetJobStateAsync(jobId, ct);

        await AssertSubstrateCountsAsync(Db, jobId, 0, ct);

        var count = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == JobEventCode.JobStateReset).CountAsync(ct);
        Assert.Equal(0, count);
    }

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    // Populates every substrate table for one job: variable/timer/signal via their operations,
    // step/result via direct inserts (no op surface creates those mid-handler).
    private async Task SeedSubstrateAsync(IDbSession db, long jobId, CancellationToken ct)
    {
        var execution = Services.GetRequiredService<IExecutionStore>();
        await CheckpointSlot.SetAsync(execution, jobId, JobCheckpointKindCode.Variable, "v.test", JobPayload.Text("x"), ct);
        await execution.ArmOrConsumeSleepTimerAsync(new ArmOrConsumeSleepTimerCommand(jobId, "t.test", 60, null), ct);
        await Services
            .GetRequiredService<ISignalStore>()
            .RaiseSignalAsync(
                new RaiseSignalCommand(
                    jobId,
                    JobCheckpointKindCode.Signal,
                    "s.test",
                    0,
                    null,
                    new JobControlInput(new JobControlActor(JobActorCode.Operator, "op"), JobEventReasonCode.JobControlManual, "seed")
                ),
                ct
            );

        await db.ExecuteRawAsync(
            "INSERT INTO {schema}.steps (job_id, name, state_code, attempt_number, result_format_id) VALUES (@p_job_id, @p_name, @p_state, 1, 0)",
            ct,
            ("@p_job_id", jobId),
            ("@p_name", "a.test"),
            ("@p_state", (byte)JobStepStateCode.Succeeded)
        );

        await db.ExecuteRawAsync(
            "INSERT INTO {schema}.results (job_id, execution_number, result_format_id, result) VALUES (@p_job_id, 1, 2, @p_result)",
            ct,
            ("@p_job_id", jobId),
            ("@p_result", Encoding.UTF8.GetBytes("x"))
        );
    }

    private static async Task AssertSubstrateCountsAsync(IDbSession db, long jobId, int expected, CancellationToken ct)
    {
        // One checkpoint per seeded kind (variable, timer, signal); expected scales per kind.
        Assert.Equal(
            expected,
            await db.From<JobCheckpoint>().Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Variable).CountAsync(ct)
        );
        Assert.Equal(
            expected,
            await db.From<JobCheckpoint>().Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Timer).CountAsync(ct)
        );
        Assert.Equal(
            expected,
            await db.From<JobCheckpoint>().Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Signal).CountAsync(ct)
        );
        Assert.Equal(expected, await db.From<JobStep>().Where(a => a.JobId == jobId).CountAsync(ct));
        Assert.Equal(expected, await db.From<JobResult>().Where(r => r.JobId == jobId).CountAsync(ct));
    }

    private static Task SetAuditLevelAsync(IDbSession db, long jobId, byte auditLevel, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET audit_level_code = @p_level WHERE id = @p_id",
            ct,
            ("@p_level", auditLevel),
            ("@p_id", jobId)
        );
}
