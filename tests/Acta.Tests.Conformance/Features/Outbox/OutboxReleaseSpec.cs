using Acta.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the token-CAS release of an unprocessed claim: the row returns to Pending with its
/// attempt instant unchanged so it is immediately reclaimable, its claim pair cleared. A stale token
/// changes nothing.
/// </summary>
[ConformanceSpec(
    "outbox-release.token-cas",
    "Release returns a claimed row to Pending, attempt unchanged, reclaimable",
    Area = "Outbox",
    Contract = "Release returns a claimed row to Pending with its next attempt unchanged so it is immediately reclaimable, only under its token.",
    Arrange = "A due source row is claimed under one token.",
    Act = "Release runs first with a stale token, then with the owning token, and a fresh claim follows.",
    Assert = "The stale release is a no-op and the owning release makes the row Pending and immediately reclaimable."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.ReleaseClaimedAsync))]
public abstract class OutboxReleaseSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A stale token no-ops and the owning token releases the row for immediate reclaim")]
    public async Task Release_is_token_cas_and_reclaimable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (id, token) = await SeedAndClaimAsync(TestKey("rel"), ct);
        var before = await Fixture.ReadOutboxRowAsync(TableName, id);

        await Store.ReleaseClaimedAsync(new FinalizeOutboxCommand(Guid.NewGuid(), [id]), ct);
        Assert.Equal((byte)OutboxStatusCode.Claimed, (await Fixture.ReadOutboxRowAsync(TableName, id)).StatusCode);

        await Store.ReleaseClaimedAsync(new FinalizeOutboxCommand(token, [id]), ct);

        var state = await Fixture.ReadOutboxRowAsync(TableName, id);
        Assert.Equal((byte)OutboxStatusCode.Pending, state.StatusCode);
        Assert.Null(state.ClaimToken);
        Assert.Equal(before.NextAttemptAtUtc, state.NextAttemptAtUtc);

        var reclaimed = await ClaimAsync(Guid.NewGuid(), batchSize: 10, ct);
        var one = Assert.Single(reclaimed);
        Assert.Equal(id, one.OutboxId);
    }
}
