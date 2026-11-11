using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the operator discard: the verb parks on the slot's discard inbox, and the next relay pass deletes the quarantined rows and records them.</summary>
[ConformanceSpec(
    "outbox-operator.discard",
    "Discard parks on the relay slot and the next pass deletes the rows",
    Area = "Outbox",
    Contract = "Discard names its rows or all, parks once per pending command on its own inbox, and the next relay pass deletes those quarantined rows and records their ids."
)]
public sealed class DiscardOutboxSpec : OutboxRelayHostBase
{
    [Fact(DisplayName = "A second discard is rejected while the first is pending, and a requeue still parks beside it")]
    public async Task Discard_parks_once_on_its_own_inbox()
    {
        var ct = TestContext.Current.CancellationToken;

        var accepted = await Outbox.DiscardAsync(TestNamespace, all: true, reasonMessage: "first", actor: "op-1", ct: ct);
        Assert.Equal(ControlAction.Accepted, accepted.Action);
        Assert.Null(accepted.PendingSinceUtc);

        var rejected = await Outbox.DiscardAsync(TestNamespace, all: true, reasonMessage: "second", actor: "op-1", ct: ct);
        Assert.Equal(ControlAction.Rejected, rejected.Action);
        Assert.NotNull(rejected.PendingSinceUtc);

        Assert.Equal(ControlAction.Accepted, (await Outbox.RequeueAsync(TestNamespace, null, null, "op-1", ct)).Action);
    }

    [Fact(DisplayName = "The relay pass deletes only the named rows, records the operator and the ids, and frees the inbox")]
    public async Task Pass_deletes_the_named_rows_and_records_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var named = await SeedQuarantinedAsync(Unique("discard-named"));
        var spared = await SeedQuarantinedAsync(Unique("discard-spared"));

        Assert.Equal(
            ControlAction.Accepted,
            (await Outbox.DiscardAsync(TestNamespace, [named], reasonMessage: "poison payload", actor: "op-1", ct: ct)).Action
        );
        var slotId = await RunRelayPassAsync(ct);

        Assert.False((await Fixture.ReadOutboxRowAsync(SourceTable, named)).Exists);
        Assert.True((await Fixture.ReadOutboxRowAsync(SourceTable, spared)).Exists);

        var evidence = await ReadSingleEventAsync(slotId, EventCode.OutboxDiscarded, ct);
        Assert.Equal("op-1", evidence.Actor);
        Assert.Null(EventDetail(evidence, "actor"));
        Assert.Contains("poison payload", evidence.EventMessage);
        Assert.Contains(named.ToString(), evidence.EventMessage);

        Assert.Equal(ControlAction.Accepted, (await Outbox.DiscardAsync(TestNamespace, all: true, ct: ct)).Action);
    }

    [Fact(DisplayName = "A discard with all deletes every quarantined row")]
    public async Task All_discards_every_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedQuarantinedAsync(Unique("discard-all-a"));
        await SeedQuarantinedAsync(Unique("discard-all-b"));

        Assert.Equal(ControlAction.Accepted, (await Outbox.DiscardAsync(TestNamespace, all: true, ct: ct)).Action);
        await RunRelayPassAsync(ct);

        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A discard without ids or all, with an empty id list, or with both throws and parks nothing")]
    public async Task Discard_takes_its_ids_or_all_exactly_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var row = await SeedQuarantinedAsync(Unique("discard-rule"));

        await Assert.ThrowsAsync<ArgumentException>(async () => await Outbox.DiscardAsync(TestNamespace, ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Outbox.DiscardAsync(TestNamespace, [], ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Outbox.DiscardAsync(TestNamespace, [], all: true, ct: ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Outbox.DiscardAsync(TestNamespace, [row], all: true, ct: ct));

        // The inbox is still free, so none of the refused calls parked a command.
        Assert.Equal(ControlAction.Accepted, (await Outbox.DiscardAsync(TestNamespace, [row], all: false, ct: ct)).Action);
    }
}
