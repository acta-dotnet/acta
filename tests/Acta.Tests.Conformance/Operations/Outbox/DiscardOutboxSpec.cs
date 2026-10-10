using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the operator discard: the verb parks on the slot's discard inbox, and the next relay pass deletes the quarantined rows and records them.</summary>
[ConformanceSpec(
    "outbox-operator.discard",
    "Discard parks on the relay slot and the next pass deletes the rows",
    Area = "Outbox",
    Contract = "Discard parks once per pending command on the slot's own discard inbox, and the next relay pass deletes the targeted quarantined rows and records their ids."
)]
public sealed class DiscardOutboxSpec : OutboxRelayHostBase
{
    [Fact(DisplayName = "A second discard is rejected while the first is pending, and a requeue still parks beside it")]
    public async Task Discard_parks_once_on_its_own_inbox()
    {
        var ct = TestContext.Current.CancellationToken;

        var accepted = await Outbox.DiscardAsync(TestNamespace, null, "first", "op-1", ct);
        Assert.Equal(ControlAction.Accepted, accepted.Action);
        Assert.Null(accepted.PendingSinceUtc);

        var rejected = await Outbox.DiscardAsync(TestNamespace, null, "second", "op-1", ct);
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

        Assert.Equal(ControlAction.Accepted, (await Outbox.DiscardAsync(TestNamespace, [named], "poison payload", "op-1", ct)).Action);
        var slotId = await RunRelayPassAsync(ct);

        Assert.False((await Fixture.ReadOutboxRowAsync(SourceTable, named)).Exists);
        Assert.True((await Fixture.ReadOutboxRowAsync(SourceTable, spared)).Exists);

        var evidence = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.JobId == slotId && e.EventCode == EventCode.OutboxDiscarded).ToListAsync(ct)
        );
        Assert.Equal("op-1", evidence.Actor);
        Assert.Contains("poison payload", evidence.ReasonMessage);
        Assert.Contains(named.ToString(), evidence.ReasonMessage);

        Assert.Equal(ControlAction.Accepted, (await Outbox.DiscardAsync(TestNamespace, null, null, null, ct)).Action);
    }

    [Fact(DisplayName = "A null scope discards every quarantined row, and an empty id list throws")]
    public async Task Null_scope_discards_every_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedQuarantinedAsync(Unique("discard-all-a"));
        await SeedQuarantinedAsync(Unique("discard-all-b"));

        await Assert.ThrowsAsync<ArgumentException>(async () => await Outbox.DiscardAsync(TestNamespace, [], null, null, ct));

        Assert.Equal(ControlAction.Accepted, (await Outbox.DiscardAsync(TestNamespace, null, null, null, ct)).Action);
        await RunRelayPassAsync(ct);

        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }
}
