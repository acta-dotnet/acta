using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the source line's counters: they come from the slot's status variable, which only a successful tick writes.</summary>
[ConformanceSpec(
    "outbox-operator.sources-status",
    "Outbox source counters come from the last successful tick",
    Area = "Outbox",
    Contract = "The source listing reads backlog and quarantine from the status a successful sys.outbox tick writes, and a failed tick leaves the previous status standing."
)]
public sealed class ListOutboxSourcesStatusSpec : OutboxRelayHostBase
{
    [Fact(DisplayName = "The source line reads the last successful tick's status, and a failed tick leaves it standing")]
    public async Task Source_line_reads_the_last_successful_ticks_status()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedQuarantinedAsync(Unique("status-q"));

        var slotId = await RunRelayPassAsync(ct);

        var settled = Assert.Single((await Outbox.ListSourcesAsync(new ListOutboxSourcesQuery(JobNamespace: TestNamespace), ct)).Items);
        Assert.Equal(0, settled.Backlog);
        Assert.Equal(1, settled.QuarantineTotal);
        Assert.Equal("claimed=0 relayed=0 dedup=0 quarantined=0 backlog=0 quarantine=1", settled.LastTickSummary);
        Assert.True(settled.IsLocal);

        // A reserved input format quarantines on contact, and a quarantine fails the tick.
        var now = DateTime.UtcNow.AddMinutes(-1);
        await Fixture.SeedOutboxRowAsync(
            SourceTable,
            new OutboxSeed(
                OutboxId: Guid.NewGuid(),
                JobNamespace: TestNamespace,
                JobName: "echo",
                InputFormatId: 4,
                Input: [1],
                DeduplicationKey: Unique("status-bad"),
                PriorityCode: null,
                CreatedAtUtc: now,
                NextAttemptAtUtc: now,
                StatusCode: (byte)OutboxStatusCode.Pending,
                FailureCount: 0
            )
        );
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(TestNamespace, slotId, ct));

        var after = Assert.Single((await Outbox.ListSourcesAsync(new ListOutboxSourcesQuery(JobNamespace: TestNamespace), ct)).Items);
        Assert.Equal(settled, after);
    }
}
