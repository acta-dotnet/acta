using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.AlertCapture;

/// <summary>
/// Conformance for the alert overlay on the recording gates at <c>Failures</c>, the leanest level: a crash still writes
/// the orphaned event alerting raises on, and a restarted one-shot's success, which the level's own recovery rule misses
/// because the restart zeroed the count, is written only under an enabled alert profile. Completion's failures and the
/// enabled profile's restarted success are pinned by <c>FailuresAuditFailureEventSpec</c>.
/// </summary>
[ConformanceSpec(
    "alert-capture.recording-overlay",
    "Failures records what alerts read, and a restarted success only under alerts",
    Area = "Alerts",
    Contract = "At Failures a lease reclaim writes the orphaned event alerts raise on, and a restarted one-shot's success is written only under an enabled alert profile."
)]
public sealed class AlertCaptureOverlaySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A lease reclaim at recording Failures writes the orphaned event, and the projector raises a FirstFailure on it")]
    public async Task Reclaim_at_recording_failures_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs SET event_recording_code = @p_level WHERE job_id = @p_id",
            ct,
            ("@p_level", (byte)EventRecordingCode.Failures),
            ("@p_id", job.JobId)
        );
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, NamespaceId, ct);
        await ChaosSpecHelpers.StrandExecutingAsync(Services, NamespaceId, workerId, job.JobId, ct);

        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, NamespaceId, ct));

        var orphaned = await ReadSingleEventAsync(job.JobId, EventCode.JobExecutionOrphaned, ct);
        Assert.Equal(EventReason.LeaseExpired, EventDetail(orphaned, "reason"));

        await AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, job.JobId, options: null, drain: null, ct);
        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, raised.Kind);
        Assert.Equal(job.JobId, raised.JobId);
    }

    [Fact(DisplayName = "A restarted one-shot's success at recording Failures with the alert profile off writes no event")]
    public async Task Restarted_success_without_alerts_is_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == NamespaceId && d.DefinitionName == "failures-audit-probe")
            .UpdateOnlyAsync(() => new DefinitionEntity { AlertProfileOverride = AlertProfileCode.None }, ct);
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 0);
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "failures-audit-probe", JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(job, ct: ct)).Action);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 2, ct);

        // The status beside the silence: an attempt that never ran would also write nothing.
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(job, ct))?.Status);
        Assert.Empty(await GetEventsByJobId.Run(Services, job.JobId, ct));
    }
}
