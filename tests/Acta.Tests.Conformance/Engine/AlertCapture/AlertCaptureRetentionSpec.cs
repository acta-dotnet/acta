using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Engine.AlertCapture;

/// <summary>
/// Conformance for retention against a projector that is behind: an alert-required event past the projector's floor
/// is owed to it, so the sweep keeps the event and its job however old they are, and both age out normally once a
/// pass consumes the event.
/// </summary>
[ConformanceSpec(
    "alert-capture.retention-floor",
    "Retention keeps owed alert events and their jobs until projected",
    Area = "Alerts",
    Contract = "Retention deletes no alert-required event, nor its job, past the projector's floor, and both age out normally once projected."
)]
public sealed class AlertCaptureRetentionSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The floor is the namespace's own sys.alerts cursor, so the slot has to exist.
    protected override bool RegisterSystemJobs => true;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A failure the projector has not consumed survives a sweep past its window, then ages out once projected")]
    public async Task Owed_failure_outlives_the_window_until_projected()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);

        // The projector has been down since before the failure, and the failure is long past every window.
        await AlertCaptureOps.AgeAsync(Db, job.JobId, ct);
        await AlertCaptureOps.SweepAsync(Services, NamespaceId, ct);

        Assert.NotNull(await Jobs.GetAsync(job, ct));
        Assert.Contains(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct), e => e.EventCode == EventCode.JobFailed);

        // The projector comes back and raises from the kept event.
        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        Assert.Equal(AlertKindCode.FinalFailure, Assert.Single(await AlertCaptureOps.AlertsAsync(Services, job.JobId, ct)).Kind);

        // Consumed, the event and its job are ordinary expired rows again.
        await AlertCaptureOps.SweepUntilGoneAsync(Services, Db, NamespaceId, job.JobId, ct);
        Assert.Null(await Jobs.GetAsync(job, ct));
        Assert.Empty(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct));
    }

    [Fact(DisplayName = "A job whose definition alerts on nothing is swept on schedule while the projector is behind")]
    public async Task Alert_disabled_definition_is_not_held()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);
        var definitionId = (await ReadJobAsync(job.JobId, ct)).DefinitionId;
        await Db.From<DefinitionEntity>()
            .Where(d => d.Id == definitionId)
            .UpdateOnlyAsync(() => new DefinitionEntity { AlertProfileOverride = AlertProfileCode.None }, ct);

        await AlertCaptureOps.AgeAsync(Db, job.JobId, ct);
        await AlertCaptureOps.SweepUntilGoneAsync(Services, Db, NamespaceId, job.JobId, ct);

        Assert.Null(await Jobs.GetAsync(job, ct));
        Assert.Empty(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct));
    }
}
