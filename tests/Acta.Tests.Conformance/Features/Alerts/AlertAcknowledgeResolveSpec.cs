using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Operator acknowledge/resolve verbs: the first write surface on <see cref="IAlerts"/>. Each verb is
/// idempotent (re-applying it is Applied without mutation, no second event) and always emits its
/// event regardless of the alert's job's audit level (low-volume operator activity). Resolve also
/// settles the alert's delivery exactly as the automatic resolve does: a queued Pending or RetryAfter
/// row becomes Suppressed with its retry instant cleared, and an already-sent row keeps its status.
/// Acknowledge settles nothing - it records that an operator has seen the alert, not that the condition
/// cleared - so a reminder already due still fires.
/// </summary>
[ConformanceSpec(
    "alert.acknowledge-resolve",
    "Operator acknowledge/resolve verbs on IAlerts.",
    Area = "Control",
    Contract = "AcknowledgeAsync and ResolveAsync stamp and audit once and are idempotent, and only ResolveAsync suppresses a queued delivery and ends the reminders.",
    Arrange = "One open alert raised in the test namespace, plus one parked mid-retry, one already delivered, and one whose reminder has come round.",
    Act = "AcknowledgeAsync/ResolveAsync are invoked once, then again, then against an unknown alert ref, and against alerts in RetryAfter and Delivered.",
    Assert = "Applied with its timestamp and one event, unchanged on the second call, NotFound for an unknown id, a resolved RetryAfter row Suppressed, a due reminder intact."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.AcknowledgeJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertManualAsync))]
public abstract class AlertAcknowledgeResolveSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "AcknowledgeAsync sets the timestamp, audits alert.acknowledged, and updates the acknowledged list filter")]
    public async Task Acknowledge_applies_and_audits()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        var result = await Operations.Alerts.AcknowledgeAsync(alertRef, "looks fine", "spec-actor", ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.NotNull(result.AcknowledgedAtUtc);
        Assert.Null(result.ResolvedAtUtc);

        var evt = await Db.From<JobEvent>()
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

        var eventCount = await Db.From<JobEvent>()
            .Where(e => e.JobId == jobId && e.EventCode == EventCode.AlertAcknowledged)
            .CountAsync(ct);
        Assert.Equal(1, eventCount);
    }

    [Fact(DisplayName = "ResolveAsync sets resolved_at_utc and audits alert.resolved without requiring a prior acknowledge")]
    public async Task Resolve_applies_and_audits_without_prior_acknowledge()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        var result = await Operations.Alerts.ResolveAsync(alertRef, "handled", "spec-actor", ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Null(result.AcknowledgedAtUtc);
        Assert.NotNull(result.ResolvedAtUtc);

        var evt = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == EventCode.AlertResolved).SingleOrDefaultAsync(ct);
        Assert.NotNull(evt);
        Assert.Equal(ActorCode.Operator, evt!.ActorCode);
        Assert.Contains($"alert {alertRef}", evt.ReasonMessage);
    }

    [Fact(DisplayName = "Re-resolving an already-resolved alert is Applied without mutation and emits no second event")]
    public async Task Reresolve_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        var first = await Operations.Alerts.ResolveAsync(alertRef, ct: ct);
        var second = await Operations.Alerts.ResolveAsync(alertRef, ct: ct);

        Assert.Equal(ControlAction.Applied, second.Action);
        Assert.Equal(first.ResolvedAtUtc, second.ResolvedAtUtc);

        var eventCount = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == EventCode.AlertResolved).CountAsync(ct);
        Assert.Equal(1, eventCount);
    }

    [Fact(DisplayName = "ResolveAsync suppresses a queued delivery and clears retry_after_utc, leaving an already-sent one alone")]
    public async Task Resolve_settles_the_queued_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IAlertStore>();

        // One alert parked mid-retry, one already delivered: the two halves of the settlement table the
        // operator verb has to honour.
        var (queuedRef, queuedJobId) = await RaiseAlertAsync(ct);
        var (sentRef, sentJobId) = await RaiseAlertAsync(ct);
        var queued = await AlertRowAsync(queuedJobId, ct);
        var sent = await AlertRowAsync(sentJobId, ct);
        Assert.True(
            await store.UpdateAlertDeliveryAsync(
                queued.Id,
                queued.Version,
                AlertDeliveryStatusCode.RetryAfter,
                retryCount: 2,
                retryAfterUtc: new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ct
            )
        );
        Assert.True(
            await store.UpdateAlertDeliveryAsync(
                sent.Id,
                sent.Version,
                AlertDeliveryStatusCode.Delivered,
                retryCount: 0,
                retryAfterUtc: null,
                ct
            )
        );

        Assert.Equal(ControlAction.Applied, (await Operations.Alerts.ResolveAsync(queuedRef, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Operations.Alerts.ResolveAsync(sentRef, ct: ct)).Action);

        // The queued notification is cancelled: the condition it was about has been declared cleared.
        var resolvedQueued = await AlertRowAsync(queuedJobId, ct);
        Assert.NotNull(resolvedQueued.ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Suppressed, resolvedQueued.DeliveryStatusCode);
        Assert.Null(resolvedQueued.RetryAfterUtc);
        Assert.Equal((byte)2, resolvedQueued.RetryCount);

        // The delivered one keeps its status: it records what actually happened to the send.
        var resolvedSent = await AlertRowAsync(sentJobId, ct);
        Assert.NotNull(resolvedSent.ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, resolvedSent.DeliveryStatusCode);
    }

    [Fact(DisplayName = "AcknowledgeAsync does not defer a reminder that is already due")]
    public async Task Acknowledge_does_not_defer_a_due_reminder()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IAlertStore>();
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        // A delivered alert whose next notification has come round.
        var row = await AlertRowAsync(jobId, ct);
        Assert.True(
            await store.UpdateAlertDeliveryAsync(
                row.Id,
                row.Version,
                AlertDeliveryStatusCode.Delivered,
                retryCount: 0,
                retryAfterUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ct
            )
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

    // Raises an alert against a freshly-enqueued job, so its auto-increment JobId is guaranteed unique
    // (unlike a fabricated constant) and scopes the alert's audit events precisely even across repeated
    // runs against a persistent live database.
    private async Task<(AlertRef AlertRef, long JobId)> RaiseAlertAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
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

        var alert = await Db.From<JobAlert>().Where(a => a.JobId == enqueued.JobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(alert);
        return (new AlertRef(alert!.AlertRef), enqueued.JobId);
    }

    private async Task<JobAlert> AlertRowAsync(long jobId, CancellationToken ct)
    {
        var alert = await Db.From<JobAlert>().Where(a => a.JobId == jobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(alert);
        return alert!;
    }
}
