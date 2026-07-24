using Acta.Features.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance that the external-outbox source store is independent of any Acta ledger: it claims and
/// finalizes purely against the source database with no ledger <c>IJobs</c>/session configured, so a
/// worker may relay a source on a provider different from its own ledger. The full mixed-provider
/// integration case is proven separately; this is the focused independence proof on each provider.
/// </summary>
[ConformanceSpec(
    "outbox-source.ledger-independent",
    "The source store round-trips with no Acta ledger configured",
    Area = "Outbox",
    Contract = "The external-outbox source store claims and deletes purely against its source database, needing no Acta ledger IJobs or session.",
    Arrange = "A source outbox table holds a due row and the container has no Acta ledger registered.",
    Act = "The source store claims the row and deletes it under the claim token.",
    Assert = "No ledger IJobs is resolvable, the claim succeeds, and the deleted row leaves the source table empty."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.ClaimDueAsync))]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.DeleteClaimedAsync))]
public abstract class OutboxSourceIndependenceSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "The source store round-trips with no ledger IJobs configured")]
    public async Task Source_store_needs_no_ledger()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(Services.GetService<IJobs>());

        var (id, token) = await SeedAndClaimAsync(TestKey("indep"), ct);
        await Store.DeleteClaimedAsync(new FinalizeOutboxCommand(token, [id]), ct);

        Assert.Equal(0, await Fixture.CountOutboxAsync(TableName));
    }
}
