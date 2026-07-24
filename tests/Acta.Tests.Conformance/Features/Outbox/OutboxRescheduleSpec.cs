using Acta.Features.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the token-CAS reschedule of a recoverable failure: the row returns to Pending with a
/// bumped failure count, a future attempt instant, and the bounded error, its claim pair cleared. A
/// stale token changes nothing.
/// </summary>
[ConformanceSpec(
    "outbox-reschedule.token-cas",
    "Reschedule returns a claimed row to Pending with backoff, only under its token",
    Area = "Outbox",
    Contract = "Reschedule returns a claimed row to Pending with a bumped failure count, a future attempt, and the error, only under its token.",
    Arrange = "A source row is claimed under one token.",
    Act = "Reschedule runs first with a stale token, then with the owning token and a backoff duration.",
    Assert = "The stale reschedule is a no-op and the owning reschedule makes the row Pending, unclaimed, and due only after source_db_now plus the backoff."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.RescheduleAsync))]
public abstract class OutboxRescheduleSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A stale token no-ops and the owning token reschedules with backoff")]
    public async Task Reschedule_is_token_cas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (id, token) = await SeedAndClaimAsync(TestKey("resched"), ct);
        // A backoff duration, not an absolute instant: the source database computes next_attempt from its
        // own clock (source_db_now + 3600s), so eligibility never depends on the ledger clock.
        const int backoffSeconds = 3600;

        var stale = new RescheduleOutboxCommand(Guid.NewGuid(), [new OutboxReschedule(id, 9, backoffSeconds, "stale")]);
        await Store.RescheduleAsync(stale, ct);
        Assert.Equal((byte)OutboxStatusCode.Claimed, (await Fixture.ReadOutboxRowAsync(TableName, id)).StatusCode);

        var valid = new RescheduleOutboxCommand(token, [new OutboxReschedule(id, 3, backoffSeconds, "route rejected")]);
        await Store.RescheduleAsync(valid, ct);

        var state = await Fixture.ReadOutboxRowAsync(TableName, id);
        Assert.Equal((byte)OutboxStatusCode.Pending, state.StatusCode);
        Assert.Null(state.ClaimToken);
        Assert.Null(state.ClaimUntilUtc);
        Assert.Equal(3, state.FailureCount);
        Assert.Equal("route rejected", state.LastError);
        // The source database anchored next_attempt to its own now + 3600s: comfortably in the future and
        // bounded near an hour out (not an unrelated ledger-clock instant).
        Assert.True(state.NextAttemptAtUtc > DateTime.UtcNow.AddMinutes(30), "next attempt was pushed into the future");
        Assert.True(
            state.NextAttemptAtUtc < DateTime.UtcNow.AddHours(2),
            "next attempt is source_db_now + backoff, not an arbitrary instant"
        );

        Assert.Empty(await ClaimAsync(Guid.NewGuid(), batchSize: 10, ct));
    }

    [Fact(DisplayName = "An error longer than 512 characters is truncated to 512 on the reschedule write")]
    public async Task Reschedule_truncates_a_long_error_to_512()
    {
        var ct = TestContext.Current.CancellationToken;
        var (id, token) = await SeedAndClaimAsync(TestKey("trunc"), ct);
        var longError = new string('x', 600);

        var valid = new RescheduleOutboxCommand(token, [new OutboxReschedule(id, 1, 3600, longError)]);
        await Store.RescheduleAsync(valid, ct);

        var state = await Fixture.ReadOutboxRowAsync(TableName, id);
        Assert.Equal(512, state.LastError!.Length);
        Assert.Equal(longError[..512], state.LastError);
    }
}
