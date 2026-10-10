using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for expired-lease recovery: a claim transaction first returns Claimed rows whose lease
/// expired to Pending, then claims them under the new token. A row held by a live lease stays claimed by
/// its owner.
/// </summary>
[ConformanceSpec(
    "outbox-claim.expired-lease-recovery",
    "A claim recovers an expired lease and reclaims it, leaving a live lease alone",
    Area = "Outbox",
    Contract = "ClaimDue recovers a Claimed row whose lease expired back to Pending and reclaims it under a new token, but never steals a live lease."
)]
public sealed class ClaimOutboxLeaseRecoverySpec : OutboxSpecBase
{
    // PostgreSQL and SQL Server reject a negative LIMIT/TOP at execution, so the claim fails after its
    // recovery statement ran; that failure must not commit the recovery.
    [Fact(DisplayName = "A claim that fails after recovering a lease rolls the recovery back")]
    public async Task Failed_claim_rolls_back_its_recovery()
    {
        Assert.SkipWhen(
            Fixture.DialectToken == "sqlite",
            "SQLite reads a negative LIMIT as no limit, so the claim cannot be made to fail."
        );
        var ct = TestContext.Current.CancellationToken;
        var oldToken = Guid.NewGuid();
        var expired = ClaimedExpiredRow(Unique("failed-claim"), oldToken);
        await Fixture.SeedOutboxRowAsync(TableName, expired);

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => ClaimAsync(Guid.NewGuid(), -1, ct));

        var state = await Fixture.ReadOutboxRowAsync(TableName, expired.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Claimed, state.StatusCode);
        Assert.Equal(oldToken, state.ClaimToken);
        Assert.Single(await ClaimAsync(Guid.NewGuid(), 1, ct));
    }

    [Fact(DisplayName = "An expired lease is recovered and reclaimed under the new token")]
    public async Task Expired_lease_is_recovered_and_reclaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        var staleToken = Guid.NewGuid();
        var expired = ClaimedExpiredRow(Unique("expired"), staleToken);
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
        var live = DueRow(Unique("live")) with
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
