using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.AlertCapture;

/// <summary>
/// Conformance for an explicit purge racing the projector: deleting a job whose alert-required event the projector
/// still owes would lose the alert, so the purge is refused loudly instead of skipped quietly.
/// </summary>
[ConformanceSpec(
    "alert-capture.purge-blocked",
    "Purge refuses a job the alert projector has not consumed",
    Area = "Alerts",
    Contract = "PurgeAsync throws JobPurgeBlockedException while the projector owes an event of the job, and purges it once that event is projected."
)]
public sealed class AlertCapturePurgeSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The floor is the namespace's own sys.alerts cursor, so the slot has to exist.
    protected override bool RegisterSystemJobs => true;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A purge before the projector consumed the job's failure is refused, and the same purge applies once it has")]
    public async Task Purge_waits_for_the_projector()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);

        await Assert.ThrowsAsync<JobPurgeBlockedException>(async () => await Jobs.PurgeAsync(job, "spec-actor", ct));
        Assert.NotNull(await Jobs.GetAsync(job, ct));
        Assert.Contains(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct), e => e.EventCode == EventCode.JobFailed);

        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Single(await AlertCaptureOps.AlertsAsync(Services, job.JobId, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(job, "spec-actor", ct)).Action);
        Assert.Equal(EventCode.JobPurged, Assert.Single(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct)).EventCode);
    }

    [Fact(DisplayName = "A job with nothing alert-required purges at once while the projector is behind")]
    public async Task Purge_of_a_clean_success_is_not_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(job, "spec-actor", ct)).Action);
    }
}
