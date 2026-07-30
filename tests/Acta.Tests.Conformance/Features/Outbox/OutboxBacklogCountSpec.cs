using Acta.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the tick-summary backlog count: only Pending rows are backlog, whether due now or
/// backed off into a later attempt, while Claimed and Quarantined rows are excluded.
/// </summary>
[ConformanceSpec(
    "outbox-backlog.count",
    "Backlog counts Pending rows only",
    Area = "Outbox",
    Contract = "CountBacklog returns the number of Pending source rows, due or backed off, excluding Claimed and Quarantined rows.",
    Arrange = "Four rows: a due Pending, a backed-off Pending, a Claimed row under a live lease, and a Quarantined row.",
    Act = "CountBacklog runs.",
    Assert = "The count is exactly the two Pending rows."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.CountBacklogAsync))]
public abstract class OutboxBacklogCountSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Pending rows count as backlog while Claimed and Quarantined rows do not")]
    public async Task Backlog_counts_pending_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("due")));
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("later"), minutesAgo: -5));
        await Fixture.SeedOutboxRowAsync(
            TableName,
            DueRow(TestKey("claimed")) with
            {
                StatusCode = (byte)OutboxStatusCode.Claimed,
                ClaimToken = Guid.NewGuid(),
                ClaimUntilUtc = DateTime.UtcNow.AddMinutes(5),
            }
        );
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("stuck"), status: (byte)OutboxStatusCode.Quarantined));

        Assert.Equal(2L, await Store.CountBacklogAsync(ct));
    }
}
