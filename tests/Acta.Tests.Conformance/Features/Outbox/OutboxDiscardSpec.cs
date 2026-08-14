using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the operator discard - the destructive exit from quarantine: the row is deleted
/// outright and the returned ids are the caller's only evidence handle, because the applying tick
/// writes them into ledger events after the proof leaves the source table. The status filter is the
/// whole guard: a Claimed in-flight row can never be discarded, even when named explicitly.
/// </summary>
[ConformanceSpec(
    "outbox-discard.operator-exit",
    "Discard deletes quarantined rows and returns the ids as the evidence handle",
    Area = "Outbox",
    Contract = "DiscardQuarantined deletes targeted (or all, when ids are null) Quarantined rows, returns the deleted ids, and never touches a row in any other status.",
    Arrange = "Two rows are quarantined and a third is claimed in-flight.",
    Act = "One quarantined row is discarded by id, then the null all-form runs, then the claimed row is named explicitly.",
    Assert = "Each discard returns exactly the deleted ids, discarded rows are gone, and the claimed row survives both the sweep and being named."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.DiscardQuarantinedAsync))]
public abstract class OutboxDiscardSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Discard deletes only quarantined rows, returns their ids, and cannot touch a claimed row")]
    public async Task Discard_deletes_quarantined_rows_and_spares_claimed_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAndQuarantineAsync(TestKey("dc-a"), "poison a", ct);
        var second = await SeedAndQuarantineAsync(TestKey("dc-b"), "poison b", ct);
        var (claimed, _) = await SeedAndClaimAsync(TestKey("dc-claimed"), ct);

        var targeted = await Store.DiscardQuarantinedAsync(new DiscardQuarantinedOutboxCommand([first]), ct);
        Assert.Equal([first], targeted);
        Assert.False((await Fixture.ReadOutboxRowAsync(TableName, first)).Exists);
        Assert.True((await Fixture.ReadOutboxRowAsync(TableName, second)).Exists);

        var swept = await Store.DiscardQuarantinedAsync(new DiscardQuarantinedOutboxCommand(null), ct);
        Assert.Equal([second], swept);

        // Naming an in-flight claimed row explicitly still deletes nothing: only status 90 qualifies.
        Assert.Empty(await Store.DiscardQuarantinedAsync(new DiscardQuarantinedOutboxCommand([claimed]), ct));
        Assert.True((await Fixture.ReadOutboxRowAsync(TableName, claimed)).Exists);
        Assert.Equal(1, await Fixture.CountOutboxAsync(TableName));
    }
}
