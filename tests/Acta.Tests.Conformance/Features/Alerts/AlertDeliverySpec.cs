using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the delivery read + settle ops (<c>GetDeliverableAlerts</c> / <c>UpdateAlertDelivery</c>):
/// a Pending alert is due by logical channel name and settles to Delivered; RetryAfter is due only after
/// its instant elapses; terminal Failed and Suppressed rows are never redelivered.
/// </summary>
[ConformanceSpec(
    "alert-delivery.read-and-settle",
    "Deliverable alerts read due rows and settle by status",
    Area = "Alerts",
    Contract = "A Pending alert settles Delivered, RetryAfter re-delivers once due, and Failed/Suppressed are terminal.",
    Arrange = "A Pending alert targeting the ops channel is raised in the test namespace.",
    Act = "GetDeliverableAlerts reads due rows by channel name and UpdateAlertDelivery settles them to Delivered, RetryAfter, Failed, or Suppressed.",
    Assert = "A Delivered alert stops being due, RetryAfter re-delivers only once its instant elapses, and Failed and Suppressed are never redelivered."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetDeliverableAlertsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.UpdateAlertDeliveryAsync))]
public abstract class AlertDeliverySpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime PastInstant = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime FutureInstant = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "Pending alert is deliverable by channel name and settles Delivered")]
    public async Task Pending_alert_is_deliverable_then_settles_delivered()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync(Db, "ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
        Assert.Equal("ops", due.ChannelName);
        Assert.Equal(AlertSeverityCode.Error, due.Severity);

        await Services
            .GetRequiredService<IAlertStore>()
            .UpdateAlertDeliveryAsync(due.AlertId, AlertDeliveryStatusCode.Delivered, due.RetryCount, retryAfterUtc: null, ct);

        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
        var row = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);
    }

    [Fact(DisplayName = "RetryAfter redelivers only when due")]
    public async Task Retryable_settle_parks_in_retry_after_and_redelivers_when_due()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync(Db, "ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));

        // Park with a future retry instant - not yet due, so the next read skips it.
        await Services
            .GetRequiredService<IAlertStore>()
            .UpdateAlertDeliveryAsync(due.AlertId, AlertDeliveryStatusCode.RetryAfter, retryCount: 1, retryAfterUtc: FutureInstant, ct);
        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));

        // Re-park with an elapsed instant - now due again, carrying the bumped retry count.
        await Services
            .GetRequiredService<IAlertStore>()
            .UpdateAlertDeliveryAsync(due.AlertId, AlertDeliveryStatusCode.RetryAfter, retryCount: 1, retryAfterUtc: PastInstant, ct);
        var again = Assert.Single(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
        Assert.Equal((byte)1, again.RetryCount);
    }

    [Fact(DisplayName = "Failed is never redelivered")]
    public async Task Terminal_failed_is_not_redelivered()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync(Db, "ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
        await Services
            .GetRequiredService<IAlertStore>()
            .UpdateAlertDeliveryAsync(due.AlertId, AlertDeliveryStatusCode.Failed, retryCount: 5, retryAfterUtc: null, ct);

        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
    }

    [Fact(DisplayName = "Suppressed is never redelivered")]
    public async Task Suppressed_is_not_redelivered()
    {
        var ct = TestContext.Current.CancellationToken;

        await RaisePendingAsync(Db, "ops", AlertSeverityCode.Error, ct);

        var due = Assert.Single(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
        await Services
            .GetRequiredService<IAlertStore>()
            .UpdateAlertDeliveryAsync(due.AlertId, AlertDeliveryStatusCode.Suppressed, retryCount: 0, retryAfterUtc: null, ct);

        Assert.Empty(await Services.GetRequiredService<IAlertStore>().GetDeliverableAlertsAsync(TestNamespaceId, 50, ct));
    }

    private Task RaisePendingAsync(IDbSession db, string channel, AlertSeverityCode severity, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId: null,
            AlertOriginCode.Automatic,
            severity,
            AlertKindCode.FinalFailure,
            title: "t",
            message: "m",
            channelName: channel,
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            ct
        );
}
