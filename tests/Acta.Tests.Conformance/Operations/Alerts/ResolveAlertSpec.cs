using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the operator resolve on IAlerts: stamp and audit once, idempotent repeat, and the queued delivery settled.</summary>
[ConformanceSpec(
    "alert.resolve",
    "Resolve stamps an alert once and settles its queued delivery",
    Area = "Control",
    Contract = "Resolve stamps and audits alert.resolved once without a prior acknowledge, repeats without a second event, and suppresses a queued delivery."
)]
public sealed class ResolveAlertSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
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

    [Fact(DisplayName = "ResolveAsync sets resolved_at_utc and audits alert.resolved without requiring a prior acknowledge")]
    public async Task Resolve_applies_and_audits_without_prior_acknowledge()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertRef, jobId) = await RaiseAlertAsync(ct);

        var result = await Operations.Alerts.ResolveAsync(alertRef, "handled", "spec-actor", ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Null(result.AcknowledgedAtUtc);
        Assert.NotNull(result.ResolvedAtUtc);

        var evt = await Db.From<EventEntity>()
            .Where(e => e.JobId == jobId && e.EventCode == EventCode.AlertResolved)
            .SingleOrDefaultAsync(ct);
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

        var eventCount = await Db.From<EventEntity>().Where(e => e.JobId == jobId && e.EventCode == EventCode.AlertResolved).CountAsync(ct);
        Assert.Equal(1, eventCount);
    }

    [Fact(DisplayName = "ResolveAsync suppresses a queued delivery and clears retry_after_utc, leaving an already-sent one alone")]
    public async Task Resolve_settles_the_queued_delivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<AlertLifecycle>();

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
        Assert.Equal(2, resolvedQueued.RetryCount);

        // The delivered one keeps its status: it records what actually happened to the send.
        var resolvedSent = await AlertRowAsync(sentJobId, ct);
        Assert.NotNull(resolvedSent.ResolvedAtUtc);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, resolvedSent.DeliveryStatusCode);
    }
}
