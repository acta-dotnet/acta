using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Alerts;

/// <summary>Conformance for the delivery settle: what one attempt's outcome writes, and the version compare-and-swap that guards it.</summary>
[ConformanceSpec(
    "update-alert-delivery.version-cas",
    "A delivery settle writes its outcome only at the version the read handed out",
    Area = "Alerts",
    Contract = "UpdateAlertDelivery writes status, retry count, and retry instant and bumps the version only while the row is still at the expected version."
)]
public sealed class UpdateAlertDeliverySpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime FutureInstant = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "A settle at the current version writes the outcome, bumps the version, and leaves the incident alone")]
    public async Task Settle_at_the_current_version_writes_the_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var raised = await RaiseAsync(ct);

        Assert.True(await SettleAsync(raised.Id, raised.Version, AlertDeliveryStatusCode.RetryAfter, 2, FutureInstant, ct));

        var settled = await RowAsync(raised.Id, ct);
        Assert.Equal(AlertDeliveryStatusCode.RetryAfter, settled.DeliveryStatusCode);
        Assert.Equal(2, settled.RetryCount);
        Assert.Equal(FutureInstant, settled.RetryAfterUtc);
        Assert.Equal(raised.Version + 1, settled.Version);
        Assert.True(settled.ModifiedAtUtc >= raised.ModifiedAtUtc, "modified_at_utc must not move backwards");
        Assert.Equal(raised.AlertRef, settled.AlertRef);
        Assert.Equal(raised.OccurrenceCount, settled.OccurrenceCount);
        Assert.Equal(raised.Title, settled.Title);
        Assert.Null(settled.ResolvedAtUtc);
        Assert.Null(settled.AcknowledgedAtUtc);
    }

    [Fact(DisplayName = "A settle at any version but the current one applies nothing, a replay of the last settle included")]
    public async Task Settle_at_another_version_applies_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var raised = await RaiseAsync(ct);
        Assert.True(await SettleAsync(raised.Id, raised.Version, AlertDeliveryStatusCode.Delivered, 0, FutureInstant, ct));
        var settled = await RowAsync(raised.Id, ct);

        Assert.False(await SettleAsync(raised.Id, raised.Version, AlertDeliveryStatusCode.Delivered, 0, FutureInstant, ct));
        Assert.False(await SettleAsync(raised.Id, settled.Version + 5, AlertDeliveryStatusCode.Failed, 9, null, ct));

        var after = await RowAsync(raised.Id, ct);
        Assert.Equal(settled.Version, after.Version);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, after.DeliveryStatusCode);
        Assert.Equal(0, after.RetryCount);
        Assert.Equal(FutureInstant, after.RetryAfterUtc);
        Assert.Equal(settled.ModifiedAtUtc, after.ModifiedAtUtc);
    }

    [Fact(DisplayName = "A settle naming an alert that does not exist applies nothing")]
    public async Task Settle_of_an_unknown_alert_applies_nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.False(await SettleAsync(long.MaxValue, 0, AlertDeliveryStatusCode.Delivered, 0, null, ct));
    }

    [Fact(DisplayName = "A null retry instant clears the one a previous settle scheduled")]
    public async Task Null_instant_clears_the_scheduled_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var raised = await RaiseAsync(ct);
        Assert.True(await SettleAsync(raised.Id, raised.Version, AlertDeliveryStatusCode.RetryAfter, 1, FutureInstant, ct));

        Assert.True(await SettleAsync(raised.Id, raised.Version + 1, AlertDeliveryStatusCode.Delivered, 0, null, ct));

        var row = await RowAsync(raised.Id, ct);
        Assert.Equal(AlertDeliveryStatusCode.Delivered, row.DeliveryStatusCode);
        Assert.Equal(0, row.RetryCount);
        Assert.Null(row.RetryAfterUtc);
        Assert.Equal(raised.Version + 2, row.Version);
    }

    private async Task<bool> SettleAsync(
        long alertId,
        int expectedVersion,
        AlertDeliveryStatusCode status,
        int retryCount,
        DateTime? retryAfterUtc,
        CancellationToken ct
    ) =>
        (
            await Services
                .GetRequiredService<IAlertsOperations>()
                .UpdateAlertDeliveryAsync(alertId, expectedVersion, status, retryCount, retryAfterUtc, ct)
        ).Updated;

    private async Task<AlertEntity> RowAsync(long alertId, CancellationToken ct) =>
        Assert.Single(await Db.From<AlertEntity>().Where(a => a.Id == alertId).ToListAsync(ct));

    private async Task<AlertEntity> RaiseAsync(CancellationToken ct)
    {
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId: null,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            title: "settle me",
            message: "one delivery attempt",
            channelName: "ops",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey: null,
            ct
        );
        return Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
    }
}
