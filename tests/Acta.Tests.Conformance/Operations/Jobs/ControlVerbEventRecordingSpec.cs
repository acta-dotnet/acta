using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for control-verb event recording gating: Pause / Resume / Cancel / Restart / RaiseSignal each
/// apply their status transition unconditionally but only insert a <c>events</c> row when the
/// job's <c>event_recording_code</c> is 20 (Audit). Proves POLICY-03 closure across all five verbs.
/// </summary>
[ConformanceSpec(
    "control.event-recording-gating",
    "Control verbs transition unconditionally but emit events only at Audit recording",
    Area = "Control",
    Contract = "Control verbs apply their status transition unconditionally and only write a job event when the job's event recording is Audit (code 20)."
)]
public sealed class ControlVerbEventRecordingSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Pause applies transition at every event recording level and emits event only at Audit")]
    public async Task Pause_applies_transition_at_every_event_recording_level_and_emits_event_only_at_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetEventRecordingAsync(Db, jobL, EventRecordingCode.Off, ct);
        await SetEventRecordingAsync(Db, jobF, EventRecordingCode.Audit, ct);

        var outcomeL = await Services.GetRequiredService<PauseJobHandler>().PauseAsync(jobL, input, ct);
        var outcomeF = await Services.GetRequiredService<PauseJobHandler>().PauseAsync(jobF, input, ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Paused, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobPaused, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Paused, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobPaused, ct));
    }

    [Fact(DisplayName = "Resume applies transition at every event recording level and emits event only at Audit")]
    public async Task Resume_applies_transition_at_every_event_recording_level_and_emits_event_only_at_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, jobL, (byte)JobStatusCode.Paused, ct);
        await SetJobStatusAsync(Db, jobF, (byte)JobStatusCode.Paused, ct);
        await SetEventRecordingAsync(Db, jobL, EventRecordingCode.Off, ct);
        await SetEventRecordingAsync(Db, jobF, EventRecordingCode.Audit, ct);

        var outcomeL = await Services.GetRequiredService<ResumeJobHandler>().ResumeAsync(jobL, input, null, ct);
        var outcomeF = await Services.GetRequiredService<ResumeJobHandler>().ResumeAsync(jobF, input, null, ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobResumed, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobResumed, ct));
    }

    [Fact(DisplayName = "Cancel applies transition at every event recording level and emits event only at Audit")]
    public async Task Cancel_applies_transition_at_every_event_recording_level_and_emits_event_only_at_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetEventRecordingAsync(Db, jobL, EventRecordingCode.Off, ct);
        await SetEventRecordingAsync(Db, jobF, EventRecordingCode.Audit, ct);

        var outcomeL = await Services.GetRequiredService<CancelJobHandler>().CancelAsync(jobL, input, fence: null, ct);
        var outcomeF = await Services.GetRequiredService<CancelJobHandler>().CancelAsync(jobF, input, fence: null, ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Cancelled, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobCancelled, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Cancelled, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "Restart applies transition at every event recording level and emits event only at Audit")]
    public async Task Restart_applies_transition_at_every_event_recording_level_and_emits_event_only_at_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, jobL, (byte)JobStatusCode.Succeeded, ct);
        await SetJobStatusAsync(Db, jobF, (byte)JobStatusCode.Succeeded, ct);
        await SetEventRecordingAsync(Db, jobL, EventRecordingCode.Off, ct);
        await SetEventRecordingAsync(Db, jobF, EventRecordingCode.Audit, ct);

        var outcomeL = await Services.GetRequiredService<RestartJobHandler>().RestartAsync(jobL, input, null, ct);
        var outcomeF = await Services.GetRequiredService<RestartJobHandler>().RestartAsync(jobF, input, null, ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobRestarted, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobRestarted, ct));
    }

    [Fact(DisplayName = "RaiseSignal upserts the signal unconditionally and emits event only at Audit")]
    public async Task RaiseSignal_upserts_signal_unconditionally_and_emits_event_only_at_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput();

        var jobL = await EnqueueAsync(ct);
        var jobF = await EnqueueAsync(ct);
        await SetEventRecordingAsync(Db, jobL, EventRecordingCode.Off, ct);
        await SetEventRecordingAsync(Db, jobF, EventRecordingCode.Audit, ct);

        var outcomeL = await Services
            .GetRequiredService<SignalsOperations>()
            .RaiseSignalAsync(new RaiseSignalSlot(jobL, CheckpointKindCode.Signal, "gate", 0, null, input), ct);
        var outcomeF = await Services
            .GetRequiredService<SignalsOperations>()
            .RaiseSignalAsync(new RaiseSignalSlot(jobF, CheckpointKindCode.Signal, "gate", 0, null, input), ct);

        Assert.Equal(ControlAction.Applied, outcomeL.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeL.Status);
        Assert.Equal(CheckpointStatusCode.Set, (await ReadSignalsAsync(jobL, ct)).Single().Status);
        Assert.Equal(0, await CountEventsAsync(jobL, EventCode.JobSignalRaised, ct));

        Assert.Equal(ControlAction.Applied, outcomeF.Action);
        Assert.Equal(JobStatusCode.Ready, outcomeF.Status);
        Assert.Equal(CheckpointStatusCode.Set, (await ReadSignalsAsync(jobF, ct)).Single().Status);
        Assert.Equal(1, await CountEventsAsync(jobF, EventCode.JobSignalRaised, ct));
    }

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static JobControlInput ControlInput() => new("test", null, "event recording gate test");

    private static Task SetEventRecordingAsync(IDbSession db, long jobId, EventRecordingCode eventRecording, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET event_recording_code = @p_level WHERE job_id = @p_id",
            ct,
            ("@p_level", (byte)eventRecording),
            ("@p_id", jobId)
        );

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        JobStateStaging.SetStatusAsync(db, jobId, statusCode, ct);
}
