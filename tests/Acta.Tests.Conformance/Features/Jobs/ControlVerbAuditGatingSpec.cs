using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for control-verb audit gating: Pause / Resume / Cancel / Restart / RaiseSignal each
/// apply their status transition unconditionally but only insert a <c>events</c> row when the
/// job's <c>audit_level_code</c> is 20 (Audit). Proves POLICY-03 closure across all five verbs.
/// </summary>
[ConformanceSpec(
    "control.audit-gating",
    "Control verbs transition unconditionally but emit events only at full audit",
    Area = "Control",
    Contract = "Control verbs apply their status transition unconditionally and only write a job event when the job's audit level is full (code 20)."
)]
public sealed class ControlVerbAuditGatingSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Pause applies transition regardless of audit level and emits event only at full audit")]
    public async Task Pause_applies_transition_regardless_of_audit_level_and_emits_event_only_at_full_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetAuditLevelAsync(Db, jobL, JobAuditLevelCode.Off, ct);
        await SetAuditLevelAsync(Db, jobF, JobAuditLevelCode.Audit, ct);

        var outcomeL = (await Services.GetRequiredService<IJobStore>().PauseJobAsync(jobL, input, ct)).Outcome;
        var outcomeF = (await Services.GetRequiredService<IJobStore>().PauseJobAsync(jobF, input, ct)).Outcome;

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Paused, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobPaused, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Paused, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobPaused, ct));
    }

    [Fact(DisplayName = "Resume applies transition regardless of audit level and emits event only at full audit")]
    public async Task Resume_applies_transition_regardless_of_audit_level_and_emits_event_only_at_full_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, jobL, (byte)JobStatusCode.Paused, ct);
        await SetJobStatusAsync(Db, jobF, (byte)JobStatusCode.Paused, ct);
        await SetAuditLevelAsync(Db, jobL, JobAuditLevelCode.Off, ct);
        await SetAuditLevelAsync(Db, jobF, JobAuditLevelCode.Audit, ct);

        var outcomeL = await Services.GetRequiredService<IJobStore>().ResumeJobAsync(jobL, input, null, ct);
        var outcomeF = await Services.GetRequiredService<IJobStore>().ResumeJobAsync(jobF, input, null, ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobResumed, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobResumed, ct));
    }

    [Fact(DisplayName = "Cancel applies transition regardless of audit level and emits event only at full audit")]
    public async Task Cancel_applies_transition_regardless_of_audit_level_and_emits_event_only_at_full_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetAuditLevelAsync(Db, jobL, JobAuditLevelCode.Off, ct);
        await SetAuditLevelAsync(Db, jobF, JobAuditLevelCode.Audit, ct);

        var outcomeL = await Services.GetRequiredService<IJobStore>().CancelJobAsync(jobL, input, ct);
        var outcomeF = await Services.GetRequiredService<IJobStore>().CancelJobAsync(jobF, input, ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, outcomeL.Outcome.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobCancelled, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, outcomeF.Outcome.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "Restart applies transition regardless of audit level and emits event only at full audit")]
    public async Task Restart_applies_transition_regardless_of_audit_level_and_emits_event_only_at_full_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, jobL, (byte)JobStatusCode.Succeeded, ct);
        await SetJobStatusAsync(Db, jobF, (byte)JobStatusCode.Succeeded, ct);
        await SetAuditLevelAsync(Db, jobL, JobAuditLevelCode.Off, ct);
        await SetAuditLevelAsync(Db, jobF, JobAuditLevelCode.Audit, ct);

        var outcomeL = await Services.GetRequiredService<IJobStore>().RestartJobAsync(jobL, input, null, ct);
        var outcomeF = await Services.GetRequiredService<IJobStore>().RestartJobAsync(jobF, input, null, ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobRestarted, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobRestarted, ct));
    }

    [Fact(DisplayName = "RaiseSignal upserts the signal unconditionally and emits event only at full audit")]
    public async Task RaiseSignal_upserts_signal_unconditionally_and_emits_event_only_at_full_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetAuditLevelAsync(Db, jobL, JobAuditLevelCode.Off, ct);
        await SetAuditLevelAsync(Db, jobF, JobAuditLevelCode.Audit, ct);

        var outcomeL = await Services
            .GetRequiredService<ISignalStore>()
            .RaiseSignalAsync(new RaiseSignalCommand(jobL, JobCheckpointKindCode.Signal, "gate", 0, null, input), ct);
        var outcomeF = await Services
            .GetRequiredService<ISignalStore>()
            .RaiseSignalAsync(new RaiseSignalCommand(jobF, JobCheckpointKindCode.Signal, "gate", 0, null, input), ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(jobL, ct)).Single().Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobSignalRaised, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(jobF, ct)).Single().Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobSignalRaised, ct));
    }

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static JobControlInput ControlInput() =>
        new(new JobControlActor(ActorCode.Operator, "test"), JobEventReasonCode.JobControlManual, "audit gate test");

    private static Task SetAuditLevelAsync(IDbSession db, long jobId, JobAuditLevelCode auditLevel, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET audit_level_code = @p_level WHERE id = @p_id",
            ct,
            ("@p_level", (byte)auditLevel),
            ("@p_id", jobId)
        );

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        RuntimeStateStaging.SetStatusAsync(db, jobId, statusCode, ct);
}
