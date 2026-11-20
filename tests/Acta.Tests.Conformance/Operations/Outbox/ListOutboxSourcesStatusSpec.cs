using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the source line's counters and its last tick instant, which come from the slot's status variable
/// every successful pass writes, and for the namespace's one quarantine alert, which follows the quarantine.
/// </summary>
[ConformanceSpec(
    "outbox-operator.sources-status",
    "Outbox source counters come from the last successful tick",
    Area = "Outbox",
    Contract = "Each successful sys.outbox pass writes the status the source line reads, and the quarantine alert is open exactly while the source holds quarantined rows."
)]
public sealed class ListOutboxSourcesStatusSpec : OutboxRelayHostBase
{
    [Fact(
        DisplayName = "The source line reads the last successful pass's status, a pass that quarantines a row included, and a failed pass leaves it standing"
    )]
    public async Task Source_line_reads_the_last_successful_passs_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedQuarantinedAsync(Unique("status-q"));

        var clock = Services.GetRequiredService<IActaClock>();
        var before = await clock.GetUtcNowAsync(ct);
        var slotId = await RunRelayPassAsync(ct);

        var settled = Assert.Single((await Outbox.ListSourcesAsync(new ListOutboxSourcesQuery(JobNamespace: TestNamespace), ct)).Items);
        Assert.Equal(0, settled.Backlog);
        Assert.Equal(1, settled.QuarantineTotal);
        Assert.InRange(settled.LastTickAtUtc!.Value, before.AddSeconds(-1), (await clock.GetUtcNowAsync(ct)).AddSeconds(1));
        Assert.True(settled.IsLocal);

        await SeedUnreadableAsync(Unique("status-bad"));
        await RunRelayPassAsync(ct);
        var quarantined = Assert.Single((await Outbox.ListSourcesAsync(new ListOutboxSourcesQuery(JobNamespace: TestNamespace), ct)).Items);
        Assert.Equal((0, 2), (quarantined.Backlog, quarantined.QuarantineTotal));

        // A source the relay cannot read fails the pass, which re-arms the firing like any attempt within its budget.
        await TestDdl.RunAsync(() => Fixture.DropOutboxAsync(SourceTable));
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(TestNamespace, slotId, ct));

        var after = Assert.Single((await Outbox.ListSourcesAsync(new ListOutboxSourcesQuery(JobNamespace: TestNamespace), ct)).Items);
        Assert.Equal(quarantined, after);
    }

    [Fact(
        DisplayName = "The quarantine alert takes the job's alert channel, collapses repeats, opens again after a resolve while rows remain, and resolves once the quarantine is empty"
    )]
    public async Task The_quarantine_alert_follows_the_quarantine()
    {
        var ct = TestContext.Current.CancellationToken;
        var relay = (await Operations.Definitions.GetAsync(TestNamespace, "sys.outbox", ct))!;
        var overrides = new DefinitionPolicyOverrides(AlertChannelName: "on-call");
        await DefinitionTestOps.UpdateOverridesAsync(Services, TestNamespace, "sys.outbox", relay.Version, overrides, null, null, ct);
        await SeedUnreadableAsync(Unique("alert-a"));
        await RunRelayPassAsync(ct);
        await SeedUnreadableAsync(Unique("alert-b"));
        await RunRelayPassAsync(ct);

        var alert = Assert.Single((await Operations.Alerts.ListAsync(new ListAlertsQuery(JobNamespace: TestNamespace), ct)).Items);
        Assert.Equal(
            (AlertKindCode.Manual, AlertSeverityCode.Critical, "on-call", 2, (DateTime?)null),
            (alert.Kind, alert.Severity, alert.ChannelName, alert.OccurrenceCount, alert.ResolvedAtUtc)
        );
        Assert.StartsWith(
            "Outbox source 'operator-src' holds 2 quarantined row(s); this pass quarantined 1: [",
            alert.Message,
            StringComparison.Ordinal
        );
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var lookup = Services.GetRequiredService<AlertsOperations>();
        Assert.Equal(alert.AlertRef, await lookup.GetAlertRefByDeduplicationKeyAsync(namespaceId, OutboxJob.QuarantineAlertKey, ct));

        // Resolved by hand with the rows still quarantined, the incident opens again on the next pass.
        await Operations.Alerts.ResolveAsync(alert.AlertRef, ct: ct);
        await RunRelayPassAsync(ct);
        var reopened = await lookup.GetAlertRefByDeduplicationKeyAsync(namespaceId, OutboxJob.QuarantineAlertKey, ct);
        Assert.NotNull(reopened);
        Assert.NotEqual(alert.AlertRef, reopened);
        Assert.Equal(
            "Outbox source 'operator-src' holds 2 quarantined row(s).",
            (await Operations.Alerts.GetAsync(reopened.Value, ct))!.Message
        );

        Assert.Equal(ControlAction.Accepted, (await Outbox.DiscardAsync(TestNamespace, all: true, ct: ct)).Action);
        await RunRelayPassAsync(ct);

        Assert.NotNull((await Operations.Alerts.GetAsync(reopened.Value, ct))!.ResolvedAtUtc);
    }

    /// <summary>Stages a due row in a reserved input format, which the next pass quarantines on contact.</summary>
    private async Task SeedUnreadableAsync(string dedup)
    {
        var now = DateTime.UtcNow.AddMinutes(-1);
        await Fixture.SeedOutboxRowAsync(
            SourceTable,
            new OutboxSeed(
                JobRef: Guid.NewGuid(),
                JobNamespace: TestNamespace,
                JobName: "echo",
                InputFormatId: 4,
                Input: [1],
                DeduplicationKey: dedup,
                PriorityCode: null,
                CreatedAtUtc: now,
                NextAttemptAtUtc: now,
                StatusCode: (byte)OutboxStatusCode.Pending,
                FailureCount: 0
            )
        );
    }
}
