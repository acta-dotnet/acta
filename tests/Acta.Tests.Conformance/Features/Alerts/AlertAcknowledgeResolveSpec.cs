using Acta.Modules.Alerting;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Operator acknowledge/resolve verbs: the first write surface on <see cref="IAlerts"/>. Each verb is
/// idempotent (re-applying it is Applied without mutation, no second event) and always emits its
/// event regardless of the alert's job's audit level (low-volume operator activity).
/// </summary>
[ConformanceSpec(
    "alert.acknowledge-resolve",
    "Operator acknowledge/resolve verbs on IAlerts.",
    Area = "Control",
    Contract = "AcknowledgeAsync/ResolveAsync set their timestamp and emit their event once, are idempotent on reapplication, and return NotFound for an unknown id.",
    Arrange = "One open alert raised in the test namespace.",
    Act = "AcknowledgeAsync/ResolveAsync are invoked once, then invoked again, then invoked against an unknown alert id.",
    Assert = "The first call is Applied with the timestamp set and one audit event, the second is Applied unchanged with the same event count, and the unknown id is NotFound."
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
        var (alertId, jobId) = await RaiseAlertAsync(ct);

        var result = await Jobs.Alerts.AcknowledgeAsync(alertId, "looks fine", "spec-actor", ct);

        Assert.Equal(JobControlAction.Applied, result.Action);
        Assert.NotNull(result.AcknowledgedAtUtc);
        Assert.Null(result.ResolvedAtUtc);

        var evt = await Db.From<JobEvent>()
            .Where(e => e.JobId == jobId && e.EventCode == JobEventCode.AlertAcknowledged)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(evt);
        Assert.Equal(JobActorCode.Operator, evt!.ActorCode);
        Assert.Equal("spec-actor", evt.ActorKey);
        Assert.Contains($"alert {alertId}", evt.ReasonMessage);
        Assert.Contains("looks fine", evt.ReasonMessage);

        var acknowledgedOnly = await Jobs.Alerts.ListAsync(new ListJobAlertsQuery(JobNamespace: TestNamespace, Acknowledged: true), ct);
        Assert.Contains(acknowledgedOnly.Items, a => a.JobAlertId == alertId);

        var unacknowledgedOnly = await Jobs.Alerts.ListAsync(new ListJobAlertsQuery(JobNamespace: TestNamespace, Acknowledged: false), ct);
        Assert.DoesNotContain(unacknowledgedOnly.Items, a => a.JobAlertId == alertId);
    }

    [Fact(DisplayName = "Re-acknowledging an already-acknowledged alert is Applied without mutation and emits no second event")]
    public async Task Reacknowledge_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertId, jobId) = await RaiseAlertAsync(ct);

        var first = await Jobs.Alerts.AcknowledgeAsync(alertId, ct: ct);
        var second = await Jobs.Alerts.AcknowledgeAsync(alertId, ct: ct);

        Assert.Equal(JobControlAction.Applied, second.Action);
        Assert.Equal(first.AcknowledgedAtUtc, second.AcknowledgedAtUtc);

        var eventCount = await Db.From<JobEvent>()
            .Where(e => e.JobId == jobId && e.EventCode == JobEventCode.AlertAcknowledged)
            .CountAsync(ct);
        Assert.Equal(1, eventCount);
    }

    [Fact(DisplayName = "ResolveAsync sets resolved_at_utc and audits alert.resolved without requiring a prior acknowledge")]
    public async Task Resolve_applies_and_audits_without_prior_acknowledge()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertId, jobId) = await RaiseAlertAsync(ct);

        var result = await Jobs.Alerts.ResolveAsync(alertId, "handled", "spec-actor", ct);

        Assert.Equal(JobControlAction.Applied, result.Action);
        Assert.Null(result.AcknowledgedAtUtc);
        Assert.NotNull(result.ResolvedAtUtc);

        var evt = await Db.From<JobEvent>()
            .Where(e => e.JobId == jobId && e.EventCode == JobEventCode.AlertResolved)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(evt);
        Assert.Equal(JobActorCode.Operator, evt!.ActorCode);
        Assert.Contains($"alert {alertId}", evt.ReasonMessage);
    }

    [Fact(DisplayName = "Re-resolving an already-resolved alert is Applied without mutation and emits no second event")]
    public async Task Reresolve_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (alertId, jobId) = await RaiseAlertAsync(ct);

        var first = await Jobs.Alerts.ResolveAsync(alertId, ct: ct);
        var second = await Jobs.Alerts.ResolveAsync(alertId, ct: ct);

        Assert.Equal(JobControlAction.Applied, second.Action);
        Assert.Equal(first.ResolvedAtUtc, second.ResolvedAtUtc);

        var eventCount = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == JobEventCode.AlertResolved).CountAsync(ct);
        Assert.Equal(1, eventCount);
    }

    [Fact(DisplayName = "AcknowledgeAsync and ResolveAsync return NotFound for an unknown alert id")]
    public async Task Unknown_alert_is_notfound()
    {
        var ct = TestContext.Current.CancellationToken;

        var ack = await Jobs.Alerts.AcknowledgeAsync(999_999_999_999L, ct: ct);
        Assert.Equal(JobControlAction.NotFound, ack.Action);
        Assert.Null(ack.AcknowledgedAtUtc);
        Assert.Null(ack.ResolvedAtUtc);

        var resolve = await Jobs.Alerts.ResolveAsync(999_999_999_999L, ct: ct);
        Assert.Equal(JobControlAction.NotFound, resolve.Action);
        Assert.Null(resolve.AcknowledgedAtUtc);
        Assert.Null(resolve.ResolvedAtUtc);
    }

    // Raises an alert against a freshly-enqueued job, so its auto-increment JobId is guaranteed unique
    // (unlike a fabricated constant) and scopes the alert's audit events precisely even across repeated
    // runs against a persistent live database.
    private async Task<(long AlertId, long JobId)> RaiseAlertAsync(CancellationToken ct)
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
            null,
            ct
        );

        var alert = await Db.From<JobAlert>().Where(a => a.JobId == enqueued.JobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(alert);
        return (alert!.Id, enqueued.JobId);
    }
}
