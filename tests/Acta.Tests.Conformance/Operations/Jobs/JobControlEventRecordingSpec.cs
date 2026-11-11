using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for what a control verb records. A verb a caller applies to one job changes what runs, so its event
/// is written whatever the job's recording level. A cancel that cascades from an ancestor and a raised signal are
/// written at volume, and only at Audit.
/// </summary>
[ConformanceSpec(
    "control.event-recording-gating",
    "Control verbs record their event at every recording level",
    Area = "Control",
    Contract = "A control verb that applies writes its event at every recording level, while a cascaded cancel and a raised signal write theirs only at Audit."
)]
public sealed class JobControlEventRecordingSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Theory(DisplayName = "A control verb that applies writes its event below Audit")]
    [InlineData("pause", EventCode.JobPaused)]
    [InlineData("resume", EventCode.JobResumed)]
    [InlineData("cancel", EventCode.JobCancelled)]
    [InlineData("restart", EventCode.JobRestarted)]
    [InlineData("reschedule", EventCode.JobRescheduled)]
    [InlineData("reprioritize", EventCode.JobReprioritized)]
    [InlineData("update-input", EventCode.JobInputAmended)]
    public async Task Control_verb_records_its_event_below_audit(string verb, EventCode recorded)
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(parentJobId: null, EventRecordingCode.Failures, ct);
        if (verb == "resume")
        {
            await JobStateStaging.SetStatusAsync(Db, job.JobId, (byte)JobStatusCode.Paused, ct);
        }
        else if (verb == "restart")
        {
            await JobStateStaging.SetStatusAsync(Db, job.JobId, (byte)JobStatusCode.Succeeded, ct);
        }

        var action = verb switch
        {
            "pause" => (await Jobs.PauseAsync(job, "why", "op", ct: ct)).Action,
            "resume" => (await Jobs.ResumeAsync(job, "why", "op", ct: ct)).Action,
            "cancel" => (await Jobs.CancelAsync(job, "why", "op", ct: ct)).Action,
            "restart" => (await Jobs.RestartAsync(job, "why", "op", ct: ct)).Action,
            "reschedule" => (await Jobs.RescheduleAsync(job, DateTime.UtcNow.AddHours(1), "why", "op", ct: ct)).Action,
            "reprioritize" => (await Jobs.ReprioritizeAsync(job, JobPriorityCode.High, "why", "op", ct: ct)).Action,
            _ => (await Jobs.UpdateInputAsync(job, JobPayload.Json(new AddNumbers(4, 5)), "why", "op", ct)).Action,
        };

        Assert.Equal(ControlAction.Applied, action);
        Assert.Equal("why", (await ReadSingleEventAsync(job.JobId, recorded, ct)).EventMessage);
    }

    [Fact(DisplayName = "A cancel is recorded on the job it names at every level, and on a descendant it reaches only at Audit")]
    public async Task Cascaded_cancel_is_recorded_only_at_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await EnqueueAsync(parentJobId: null, EventRecordingCode.Failures, ct);
        var quietChild = await EnqueueAsync(parent.JobId, EventRecordingCode.Failures, ct);
        var auditedChild = await EnqueueAsync(parent.JobId, EventRecordingCode.Audit, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(parent, "why", "op", ct: ct)).Action);

        // One cancel of a wide tree must not write an event per descendant where the definitions asked for quiet.
        Assert.Equal(1, await CountEventsAsync(parent.JobId, EventCode.JobCancelled, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(quietChild.JobId, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(quietChild.JobId, EventCode.JobCancelled, ct));
        Assert.Equal(1, await CountEventsAsync(auditedChild.JobId, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "A caller's cancel of an executing job below Audit records the cancel and not the attempt's yield")]
    public async Task Cancel_of_an_executing_job_below_audit_records_no_yield()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(parentJobId: null, EventRecordingCode.Failures, ct);
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = Assert.IsType<WorkerEntity>(await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct));
        var claim = await Services
            .GetRequiredService<ExecutionOperations>()
            .ClaimJobAsync(new ClaimOptions(ns, worker.Id, MaxBatch: 1), 30, job.JobId, ct);
        Assert.Single(claim.Jobs);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(job, "why", "op", ct: ct)).Action);

        // The yield is the attempt's outcome, which this level records only for a failure.
        Assert.Equal(1, await CountEventsAsync(job.JobId, EventCode.JobCancelled, ct));
        Assert.Equal(0, await CountEventsAsync(job.JobId, EventCode.JobExecutionYielded, ct));
    }

    [Fact(DisplayName = "A raised signal sets its checkpoint at every recording level and writes its event only at Audit")]
    public async Task RaiseSignal_upserts_signal_unconditionally_and_emits_event_only_at_audit()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = new JobControlInput("test", null, "event recording gate test");
        var quietId = (await EnqueueAsync(parentJobId: null, EventRecordingCode.Failures, ct)).JobId;
        var auditedId = (await EnqueueAsync(parentJobId: null, EventRecordingCode.Audit, ct)).JobId;
        var signals = Services.GetRequiredService<SignalsOperations>();

        var quiet = await signals.RaiseJobSignalAsync(
            new RaiseJobSignalCheckpoint(quietId, CheckpointKindCode.Signal, "gate", 0, null, input),
            ct
        );
        var audited = await signals.RaiseJobSignalAsync(
            new RaiseJobSignalCheckpoint(auditedId, CheckpointKindCode.Signal, "gate", 0, null, input),
            ct
        );

        Assert.Equal(ControlAction.Applied, quiet.Action);
        Assert.Equal(CheckpointStatusCode.Set, (await ReadSignalsAsync(quietId, ct)).Single().Status);
        Assert.Equal(0, await CountEventsAsync(quietId, EventCode.JobSignalRaised, ct));

        Assert.Equal(ControlAction.Applied, audited.Action);
        Assert.Equal(CheckpointStatusCode.Set, (await ReadSignalsAsync(auditedId, ct)).Single().Status);
        Assert.Equal(1, await CountEventsAsync(auditedId, EventCode.JobSignalRaised, ct));
    }

    private async Task<EnqueueJobResponse> EnqueueAsync(long? parentJobId, EventRecordingCode recording, CancellationToken ct)
    {
        var request = new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3)), ParentJobId: parentJobId);
        var job = await Jobs.EnqueueAsync(request, ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET event_recording_code = @p_level WHERE job_id = @p_id",
            ct,
            ("@p_level", (byte)recording),
            ("@p_id", job.JobId)
        );
        return job;
    }
}
