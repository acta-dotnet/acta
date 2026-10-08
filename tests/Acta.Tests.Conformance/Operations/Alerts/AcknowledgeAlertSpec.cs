using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the operator acknowledge on IAlerts: stamp and audit once, idempotent repeat, no deferral of a due reminder.</summary>
[ConformanceSpec(
    "alert.acknowledge",
    "Acknowledge stamps and audits an alert once",
    Area = "Control",
    Contract = "Acknowledge stamps and audits alert.acknowledged once, repeats without a second event, keeps a due reminder due, and reports NotFound for an unknown ref."
)]
public sealed class AcknowledgeAlertSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Raises an alert against a freshly-enqueued job, so its auto-increment JobId is guaranteed unique
    // (unlike a fabricated constant) and scopes the alert's audit events precisely even across repeated
    // runs against a persistent live database.
    private async Task<(AlertRef AlertRef, long JobId)> RaiseAlertAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            enqueued.JobId,
            AlertOriginCode.Manual,
            AlertSeverityCode.Info,
            AlertKindCode.Manual,
            "ack-resolve-spec alert",
            "ack-resolve-spec message",
            "default",
            AlertDeliveryStatusCode.Pending,
            null,
            ct
        );

        var alert = await Db.From<AlertEntity>().Where(a => a.JobId == enqueued.JobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(alert);
        return (new AlertRef(alert!.AlertRef), enqueued.JobId);
    }

    private async Task<AlertEntity> AlertRowAsync(long jobId, CancellationToken ct)
    {
        var alert = await Db.From<AlertEntity>().Where(a => a.JobId == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(alert);
        return alert!;
    }

    [Fact(DisplayName = "AcknowledgeAsync sets the timestamp, audits alert.acknowledged, and updates the acknowledged list filter")]
    public async Task Acknowledge_applies_and_audits()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        var result = await Operations.Alerts.AcknowledgeAsync(alertRef, "looks fine", "spec-actor", ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.NotNull(result.AcknowledgedAtUtc);
        Assert.Null(result.ResolvedAtUtc);

        var evt = await Db.From<EventEntity>()
            .Where(e => e.JobId == jobId && e.EventCode == EventCode.AlertAcknowledged)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(evt);
        Assert.Equal(ActorCode.Operator, evt!.ActorCode);
        Assert.Equal("spec-actor", evt.ActorKey);
        Assert.Contains($"alert {alertRef}", evt.ReasonMessage);
        Assert.Contains("looks fine", evt.ReasonMessage);

        var acknowledgedOnly = await Operations.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: TestNamespace, AcknowledgedOnly: true),
            ct
        );
        Assert.Contains(acknowledgedOnly.Items, a => a.AlertRef == alertRef);

        var unacknowledgedOnly = await Operations.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: TestNamespace, AcknowledgedOnly: false),
            ct
        );
        Assert.DoesNotContain(unacknowledgedOnly.Items, a => a.AlertRef == alertRef);
    }

    [Fact(DisplayName = "Re-acknowledging an already-acknowledged alert is Applied without mutation and emits no second event")]
    public async Task Reacknowledge_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        var first = await Operations.Alerts.AcknowledgeAsync(alertRef, ct: ct);
        var second = await Operations.Alerts.AcknowledgeAsync(alertRef, ct: ct);

        Assert.Equal(ControlAction.Applied, second.Action);
        Assert.Equal(first.AcknowledgedAtUtc, second.AcknowledgedAtUtc);

        var eventCount = await Db.From<EventEntity>()
            .Where(e => e.JobId == jobId && e.EventCode == EventCode.AlertAcknowledged)
            .CountAsync(ct);
        Assert.Equal(1, eventCount);
    }

    [Fact(DisplayName = "AcknowledgeAsync does not defer a reminder that is already due")]
    public async Task Acknowledge_does_not_defer_a_due_reminder()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<AlertLifecycle>();
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        // A delivered alert whose next notification has come round.
        var row = await AlertRowAsync(jobId, ct);
        Assert.True(
            (
                await store.UpdateAlertDeliveryAsync(
                    row.Id,
                    row.Version,
                    AlertDeliveryStatusCode.Delivered,
                    retryCount: 0,
                    retryAfterUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ct
                )
            ).Updated
        );
        Assert.Contains(
            await store.GetDeliverableAlertsAsync(Runtime.RegisteredNamespaceIds[TestNamespace], 50, ct),
            a => a.AlertId == row.Id
        );

        Assert.Equal(ControlAction.Applied, (await Operations.Alerts.AcknowledgeAsync(alertRef, ct: ct)).Action);

        // Acknowledging says an operator has seen it, not that the condition cleared - only resolving says
        // that. It bumps the row (modified_at_utc, version), and the reminder still fires: an alert nobody
        // has fixed must not go quiet because somebody clicked it.
        var acknowledged = await AlertRowAsync(jobId, ct);
        Assert.NotNull(acknowledged.AcknowledgedAtUtc);
        Assert.Null(acknowledged.ResolvedAtUtc);
        Assert.Contains(
            await store.GetDeliverableAlertsAsync(Runtime.RegisteredNamespaceIds[TestNamespace], 50, ct),
            a => a.AlertId == row.Id
        );
    }

    [Fact(DisplayName = "AcknowledgeAsync and ResolveAsync return NotFound for an unknown alert ref")]
    public async Task Unknown_alert_is_notfound()
    {
        var ct = TestContext.Current.CancellationToken;

        var ack = await Operations.Alerts.AcknowledgeAsync(AlertRef.New(), ct: ct);
        Assert.Equal(ControlAction.NotFound, ack.Action);
        Assert.Null(ack.AcknowledgedAtUtc);
        Assert.Null(ack.ResolvedAtUtc);

        var resolve = await Operations.Alerts.ResolveAsync(AlertRef.New(), ct: ct);
        Assert.Equal(ControlAction.NotFound, resolve.Action);
        Assert.Null(resolve.AcknowledgedAtUtc);
        Assert.Null(resolve.ResolvedAtUtc);
    }
}
