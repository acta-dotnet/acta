using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the token-CAS quarantine: an exhausted or non-recoverable row is retained (not
/// deleted) at status 90 with its error, excluded from normal claims until an operator acts. A stale
/// token changes nothing.
/// </summary>
[ConformanceSpec(
    "outbox-quarantine.token-cas",
    "Quarantine retains a claimed row at status 90 and excludes it from claims",
    Area = "Outbox",
    Contract = "Quarantine retains a claimed row at status 90 with its error and clears the claim pair, only under its token, excluding it from claims.",
    Arrange = "A source row is claimed under one token.",
    Act = "Quarantine runs first with a stale token, then with the owning token.",
    Assert = "The stale quarantine is a no-op and the owning quarantine retains the row at status 90 and it is never reclaimed."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.QuarantineAsync))]
public abstract class OutboxQuarantineSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A stale token no-ops and the owning token quarantines and retains the row")]
    public async Task Quarantine_is_token_cas_and_retained()
    {
        var ct = TestContext.Current.CancellationToken;
        var (id, token) = await SeedAndClaimAsync(TestKey("quar"), ct);

        await Store.QuarantineAsync(new QuarantineOutboxCommand(Guid.NewGuid(), [new OutboxQuarantine(id, 5, "stale")]), ct);
        Assert.Equal((byte)OutboxStatusCode.Claimed, (await Fixture.ReadOutboxRowAsync(TableName, id)).StatusCode);

        await Store.QuarantineAsync(new QuarantineOutboxCommand(token, [new OutboxQuarantine(id, 5, "poison payload")]), ct);

        var state = await Fixture.ReadOutboxRowAsync(TableName, id);
        Assert.True(state.Exists);
        Assert.Equal((byte)OutboxStatusCode.Quarantined, state.StatusCode);
        Assert.Null(state.ClaimToken);
        Assert.Equal(5, state.FailureCount);
        Assert.Equal("poison payload", state.LastError);

        Assert.Empty(await ClaimAsync(Guid.NewGuid(), batchSize: 10, ct));
    }
}
