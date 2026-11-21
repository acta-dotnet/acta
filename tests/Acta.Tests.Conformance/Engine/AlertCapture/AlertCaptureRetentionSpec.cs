using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.AlertCapture;

/// <summary>
/// Conformance for retention against a projector that is behind: retention judges a namespace's jobs and events at the
/// earlier of its cutoff and the instant the projector has read to, so what the projector has not passed is kept
/// however old it is, and ages out normally once a pass reads past it. Nothing else waits for the projector.
/// </summary>
[ConformanceSpec(
    "alert-capture.retention-floor",
    "Retention waits for the alert projector before deleting jobs and events",
    Area = "Alerts",
    Contract = "Retention deletes no job or event the namespace's alert projector has not passed, and both age out normally once it passes them."
)]
[Deploys("job-throw-not-implemented")]
public sealed class AlertCaptureRetentionSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<AlertCaptureRetentionSpec>>
{
    // The floor is the namespace's own sys.alerts cursor, so the slot has to exist.
    protected override bool RunSystemJobs => true;

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

    [Fact(DisplayName = "A job whose definition alerts on nothing waits for the projector to pass its retention, then is swept")]
    public async Task Alert_disabled_definition_waits_for_the_projector_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);
        var definitionId = (await ReadJobAsync(job.JobId, ct)).DefinitionId;
        await Db.From<DefinitionEntity>()
            .Where(d => d.Id == definitionId)
            .UpdateOnlyAsync(() => new DefinitionEntity { AlertProfileOverride = AlertProfileCode.None }, ct);

        // The projector owes this job nothing, but retention reads one instant for the namespace, not per job.
        await AlertCaptureOps.AgeAsync(Db, job.JobId, ct);
        await AlertCaptureOps.SweepAsync(Services, NamespaceId, ct);
        Assert.NotNull(await Jobs.GetAsync(job, ct));
        Assert.NotEmpty(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct));

        await AlertCaptureOps.ProjectAsync(Services, TestNamespace, NamespaceId, ct);
        await AlertCaptureOps.SweepUntilGoneAsync(Services, Db, NamespaceId, job.JobId, ct);

        Assert.Null(await Jobs.GetAsync(job, ct));
        Assert.Empty(await AlertCaptureOps.JobEventsAsync(Db, job.JobId, ct));
    }

    [Fact(DisplayName = "A projector whose position lies in the past holds back the jobs and events it has not passed, and nothing else")]
    public async Task A_projector_in_the_past_holds_back_jobs_and_events_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var position = DateTime.UtcNow.AddDays(-10 * AlertCaptureOps.RetentionDays);
        var passed = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);
        var ahead = await AlertCaptureOps.FailTerminallyAsync(Runtime, Jobs, TestNamespace, ct);
        // Both past every window: one a day before the projector's position, the other a day after it.
        await AlertCaptureOps.AgeAsync(Db, passed.JobId, position.AddDays(-1), ct);
        await AlertCaptureOps.AgeAsync(Db, ahead.JobId, position.AddDays(1), ct);
        // An alert and a lock past their windows, where the projector's position is no reason to wait.
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId: null,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "held-back",
            message: "held-back",
            channelName: "default",
            AlertDeliveryStatusCode.Delivered,
            deduplicationKey: null,
            ct
        );
        var alertAgedAt = position.AddDays(1);
        await Db.From<AlertEntity>()
            .Where(a => a.NamespaceId == NamespaceId)
            .UpdateOnlyAsync(() => new AlertEntity { CreatedAtUtc = alertAgedAt }, ct);
        var lockKey = Unique("held-back.lock");
        Assert.NotNull(
            (
                await Services.GetRequiredService<LocksOperations>().AcquireLockAsync(lockKey, TimeSpan.FromSeconds(-1), ownerJobId: -1, ct)
            ).Token
        );
        var projector = await AlertCaptureOps.ProjectorAsync(Services, TestNamespace, ct);
        await AlertTestOps.SetAlertsCursorAsync(Services, TestNamespace, NamespaceId, projector, new AlertsCursor(position.Ticks, 0), ct);

        await RetentionTestOps.PurgeUntilAsync(
            Services,
            NamespaceId,
            AlertCaptureOps.RetentionDays,
            1000,
            50,
            async () =>
                await Jobs.GetAsync(passed, ct) is null
                && (await AlertCaptureOps.JobEventsAsync(Db, passed.JobId, ct)).Count == 0
                && (await Db.From<AlertEntity>().Where(a => a.NamespaceId == NamespaceId).ToListAsync(ct)).Count == 0
                && await Db.From<Acta.LockEntity>().Where(l => l.LockKey == lockKey).SingleOrDefaultAsync(ct) is null,
            ct
        );

        Assert.Null(await Jobs.GetAsync(passed, ct));
        Assert.Empty(await AlertCaptureOps.JobEventsAsync(Db, passed.JobId, ct));
        Assert.Empty(await Db.From<AlertEntity>().Where(a => a.NamespaceId == NamespaceId).ToListAsync(ct));
        Assert.Null(await Db.From<Acta.LockEntity>().Where(l => l.LockKey == lockKey).SingleOrDefaultAsync(ct));
        Assert.NotNull(await Jobs.GetAsync(ahead, ct));
        Assert.Contains(await AlertCaptureOps.JobEventsAsync(Db, ahead.JobId, ct), e => e.EventCode == EventCode.JobFailed);
    }
}
