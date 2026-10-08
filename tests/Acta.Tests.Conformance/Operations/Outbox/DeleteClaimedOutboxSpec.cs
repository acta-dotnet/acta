using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the token-CAS delete of safely-ingested rows: only the current claim's token may
/// remove a row, so a stale relay that lost its claim cannot delete work another relay reclaimed.
/// </summary>
[ConformanceSpec(
    "outbox-delete.token-cas",
    "Delete removes a claimed row only under its token, a stale token no-ops",
    Area = "Outbox",
    Contract = "DeleteClaimed removes a claimed row only when the command token matches the row's claim token, and a stale token deletes nothing."
)]
public sealed class DeleteClaimedOutboxSpec : OutboxSpecBase
{
    [Fact(DisplayName = "A stale token deletes nothing and the owning token deletes the row")]
    public async Task Delete_is_token_cas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (id, token) = await SeedAndClaimAsync(TestKey("del"), ct);

        await Store.DeleteClaimedAsync(new DeleteClaimedOutboxCommand(Guid.NewGuid(), [id]), ct);
        var afterStale = await Fixture.ReadOutboxRowAsync(TableName, id);
        Assert.True(afterStale.Exists);
        Assert.Equal((byte)OutboxStatusCode.Claimed, afterStale.StatusCode);

        await Store.DeleteClaimedAsync(new DeleteClaimedOutboxCommand(token, [id]), ct);
        Assert.False((await Fixture.ReadOutboxRowAsync(TableName, id)).Exists);
        Assert.Equal(0, await Fixture.CountOutboxAsync(TableName));
    }
}
