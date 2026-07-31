using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for expired-lease recovery: a claim transaction first returns Claimed rows whose lease
/// expired to Pending, then claims them under the new token. A row held by a live lease stays claimed by
/// its owner.
/// </summary>
[ConformanceSpec(
    "outbox-claim.expired-lease-recovery",
    "A claim recovers an expired lease and reclaims it, leaving a live lease alone",
    Area = "Outbox",
    Contract = "ClaimDue recovers a Claimed row whose lease expired back to Pending and reclaims it under a new token, but never steals a live lease.",
    Arrange = "A source row is Claimed with an expired lease, and another is Claimed with a live lease.",
    Act = "ClaimDue runs with a fresh token.",
    Assert = "The expired row is reclaimed under the new token while the live-lease row keeps its owner and token."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.ClaimDueAsync))]
public abstract class OutboxLeaseRecoverySpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "An expired lease is recovered and reclaimed under the new token")]
    public async Task Expired_lease_is_recovered_and_reclaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        var staleToken = Guid.NewGuid();
        var expired = ClaimedExpiredRow(TestKey("expired"), staleToken);
        await Fixture.SeedOutboxRowAsync(TableName, expired);

        var freshToken = Guid.NewGuid();
        var claimed = await ClaimAsync(freshToken, batchSize: 10, ct);

        var one = Assert.Single(claimed);
        Assert.Equal(expired.OutboxId, one.OutboxId);

        var state = await Fixture.ReadOutboxRowAsync(TableName, expired.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Claimed, state.StatusCode);
        Assert.Equal(freshToken, state.ClaimToken);
        Assert.NotEqual(staleToken, state.ClaimToken);
    }

    [Fact(DisplayName = "A live lease is not stolen by a competing claim")]
    public async Task Live_lease_is_not_stolen()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = Guid.NewGuid();
        var live = DueRow(TestKey("live")) with
        {
            StatusCode = (byte)OutboxStatusCode.Claimed,
            ClaimToken = owner,
            ClaimUntilUtc = DateTime.UtcNow.AddMinutes(30),
        };
        await Fixture.SeedOutboxRowAsync(TableName, live);

        var claimed = await ClaimAsync(Guid.NewGuid(), batchSize: 10, ct);

        Assert.Empty(claimed);
        var state = await Fixture.ReadOutboxRowAsync(TableName, live.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Claimed, state.StatusCode);
        Assert.Equal(owner, state.ClaimToken);
    }
}
