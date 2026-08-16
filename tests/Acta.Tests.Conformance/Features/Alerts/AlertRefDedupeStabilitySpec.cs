using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the public identity of a deduplicated alert: the ref minted by the first firing is
/// the alert's ref for the life of the row. Every later firing inside the window takes the UPDATE arm
/// of the upsert, which bumps <c>occurrence_count</c> and refreshes the prose but never touches
/// <c>alert_ref</c> - so a link an operator followed, or a transport already delivered, keeps resolving.
/// </summary>
[ConformanceSpec(
    "alert-ref.survives-dedupe",
    "A deduplicated alert keeps the ref its first firing minted",
    Area = "Alerts",
    Contract = "A repeat firing inside the dedupe window bumps occurrence_count on the existing row and leaves its public alert ref exactly as the first firing minted it.",
    Arrange = "A seeded job and definition give the alert a subject, and one deduplication key with a fixed window bucket collapses every firing onto one row.",
    Act = "The same deduplication key is raised three times inside one window while a second key is raised alongside it.",
    Assert = "Occurrence count grows with each firing while the alert ref is unchanged, and the second key mints a ref of its own."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
public abstract class AlertRefDedupeStabilitySpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    // A fixed bucket start, so every firing lands in the same window without depending on wall-clock
    // timing: the window is an argument to the raise, not something the test has to wait out.
    private static readonly DateTime Window = new(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);

    private long JobIdValue;

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        var ct = TestContext.Current.CancellationToken;
        var definitionId = await Seeder.SeedJobDefinitionAsync(TestNamespaceId, "alert-ref-def", ct);
        (JobIdValue, _) = await Seeder.SeedJobAsync(TestNamespaceId, definitionId, ct: ct);
    }

    [Fact(DisplayName = "Repeat firings inside the dedupe window bump occurrence_count and keep the ref the first firing minted")]
    public async Task Repeat_firings_keep_the_first_minted_alert_ref()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("ref-stability");

        await RaiseAsync(key, "first", ct);
        var first = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        var minted = first.AlertRef;
        Assert.NotEqual(Guid.Empty, minted);
        Assert.Equal(1, first.OccurrenceCount);

        await RaiseAsync(key, "second", ct);
        var second = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(2, second.OccurrenceCount);
        Assert.Equal(minted, second.AlertRef);

        // A third firing, because a ref that survives one repeat but is re-minted on the next would
        // still break every link an operator kept - stability is the property, not one comparison.
        await RaiseAsync(key, "third", ct);
        var third = Assert.Single(await ReadAlertsAsync(TestNamespaceId, ct));
        Assert.Equal(3, third.OccurrenceCount);
        Assert.Equal(minted, third.AlertRef);
        Assert.Equal(first.Id, third.Id);

        // The row identity is what carries the ref, not the raise call: a different key is a different
        // alert and gets its own ref. Without this the assertions above would also pass if the raise
        // path minted one ref per namespace.
        await RaiseAsync(TestKey("ref-stability-other"), "other", ct);
        var rows = await ReadAlertsAsync(TestNamespaceId, ct);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(r => r.AlertRef).Distinct().Count());
    }

    private Task RaiseAsync(string deduplicationKey, string title, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            JobIdValue,
            AlertOriginCode.Manual,
            AlertSeverityCode.Warning,
            AlertKindCode.Manual,
            title,
            "message",
            channelName: "ops",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey,
            Window,
            ct
        );
}
