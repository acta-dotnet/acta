using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
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
    Contract = "Control verbs apply their status transition unconditionally and only write a job event when the job's audit level is full (code 20).",
    Arrange = "For each control verb two jobs are enqueued, one set to audit level Off and one to full Audit.",
    Act = "Pause, Resume, Cancel, Restart, and RaiseSignal are invoked against both jobs of each pair.",
    Assert = "Both jobs apply the status transition but only the full-audit job gains a verb event row."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.PauseJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ResumeJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RestartJobAsync))]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.RaiseSignalAsync))]
public abstract class ControlVerbAuditGatingSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
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

        var outcomeL = await Services.GetRequiredService<IJobStore>().PauseJobAsync(jobL, input, ct);
        var outcomeF = await Services.GetRequiredService<IJobStore>().PauseJobAsync(jobF, input, ct);

        Assert.Equal(JobControlActionInternal.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Paused, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, JobEventCode.JobPaused, ct));

        Assert.Equal(JobControlActionInternal.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Paused, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, JobEventCode.JobPaused, ct));
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

        Assert.Equal(JobControlActionInternal.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, JobEventCode.JobResumed, ct));

        Assert.Equal(JobControlActionInternal.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, JobEventCode.JobResumed, ct));
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

        Assert.Equal(JobControlActionInternal.Applied, outcomeL.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, outcomeL.Outcome.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, JobEventCode.JobCancelled, ct));

        Assert.Equal(JobControlActionInternal.Applied, outcomeF.Outcome.Action);
        Assert.Equal(JobStatusCode.Cancelled, outcomeF.Outcome.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, JobEventCode.JobCancelled, ct));
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

        Assert.Equal(JobControlActionInternal.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, JobEventCode.JobRestarted, ct));

        Assert.Equal(JobControlActionInternal.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, JobEventCode.JobRestarted, ct));
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

        Assert.Equal(JobControlActionInternal.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(jobL, ct)).Single().Status);
        Assert.Equal(0, await CountEventsAsync(jobL, JobEventCode.JobSignalRaised, ct));

        Assert.Equal(JobControlActionInternal.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(jobF, ct)).Single().Status);
        Assert.Equal(1, await CountEventsAsync(jobF, JobEventCode.JobSignalRaised, ct));
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

    private static JobControlInput ControlInput() =>
        new(new JobControlActor(JobActorCode.Operator, "test"), JobEventReasonCode.JobControlManual, "audit gate test");

    private static Task SetAuditLevelAsync(IDbSession db, long jobId, JobAuditLevelCode auditLevel, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET audit_level_code = @p_level WHERE id = @p_id",
            ct,
            ("@p_level", (byte)auditLevel),
            ("@p_id", jobId)
        );

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status WHERE job_id = @p_id",
            ct,
            ("@p_status", statusCode),
            ("@p_id", jobId)
        );
}
