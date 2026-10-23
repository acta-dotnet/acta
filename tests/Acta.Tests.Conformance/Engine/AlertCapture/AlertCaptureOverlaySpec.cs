using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.AlertCapture;

/// <summary>
/// Conformance for the alert overlay on the recording gates: a definition whose alert profile is enabled writes the
/// events alerting needs whatever the job's recording level, so a crash under recording Off still alerts and a
/// restarted job's success still resolves. Crash recovery and the restart are pinned here; completion's failures share
/// the gate and are pinned by <c>FailuresAuditFailureEventSpec</c>.
/// </summary>
[ConformanceSpec(
    "alert-capture.recording-overlay",
    "An enabled alert profile records alert-required events at any level",
    Area = "Alerts",
    Contract = "At recording Off an alert-enabled job still writes its orphaned event and its restarted success, and a disabled profile writes nothing."
)]
public sealed class AlertCaptureOverlaySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A lease reclaim at recording Off writes the orphaned event, and the projector raises a FirstFailure on it")]
    public async Task Reclaim_at_recording_off_still_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await StrandAtRecordingOffAsync(ct);

        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, NamespaceId, ct));

        var orphaned = await ReadSingleEventAsync(job.JobId, EventCode.JobExecutionOrphaned, ct);
        Assert.Equal(EventReason.LeaseExpired, EventDetail(orphaned, "reason"));

        await AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, job.JobId, options: null, drain: null, ct);
        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, raised.Kind);
        Assert.Equal(job.JobId, raised.JobId);
    }

    [Fact(DisplayName = "The same reclaim with the alert profile off writes no event at recording Off")]
    public async Task Reclaim_at_recording_off_without_alerts_is_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == NamespaceId && d.DefinitionName == "add-numbers")
            .UpdateOnlyAsync(() => new DefinitionEntity { AlertProfileOverride = AlertProfileCode.None }, ct);
        var job = await StrandAtRecordingOffAsync(ct);

        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, NamespaceId, ct));

        Assert.Equal(1, (await ReadJobAsync(job.JobId, ct)).FailureCount);
        Assert.Empty(await GetEventsByJobId.Run(Services, job.JobId, ct));
    }

    [Fact(DisplayName = "A restarted job's success at recording Off is written, and it resolves the incident the failure opened")]
    public async Task Restarted_success_at_recording_off_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        const int budget = 6;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: budget);
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None), ct);
        await RecordNothingAsync(job.JobId, ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), budget, ct);

        await AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, job.JobId, options: null, drain: null, ct);
        Assert.Null(Assert.Single(await ReadAlertsAsync(NamespaceId, ct), a => a.Kind == AlertKindCode.FinalFailure).ResolvedAtUtc);

        // Restart zeroed the count, so only the attempt number says this success answers something.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(job, ct: ct)).Action);
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 0);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);
        Assert.Equal(1, await CountEventsAsync(job.JobId, EventCode.JobExecutionSucceeded, ct));

        await AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, job.JobId, options: null, drain: null, ct);
        Assert.All(await ReadAlertsAsync(NamespaceId, ct), a => Assert.NotNull(a.ResolvedAtUtc));
    }

    // A started attempt under a lapsed lease, on a job that records nothing of its own.
    private async Task<EnqueueJobResponse> StrandAtRecordingOffAsync(CancellationToken ct)
    {
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        await RecordNothingAsync(job.JobId, ct);
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, NamespaceId, ct);
        await ChaosSpecHelpers.StrandExecutingAsync(Services, NamespaceId, workerId, job.JobId, ct);
        return job;
    }

    private Task RecordNothingAsync(long jobId, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET event_recording_code = @p_level WHERE job_id = @p_id",
            ct,
            ("@p_level", (byte)EventRecordingCode.Off),
            ("@p_id", jobId)
        );
}
