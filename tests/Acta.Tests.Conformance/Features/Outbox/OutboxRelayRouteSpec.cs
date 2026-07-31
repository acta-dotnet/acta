using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// A row whose target route is not yet registered is a recoverable routing rejection: it reschedules
/// quietly with a bumped failure count and a future attempt, then delivers exactly once after a worker
/// for that namespace registers the route. This is the "unknown route waits, not poisons" boundary: an
/// undeployed consumer is a retry with backoff, not a torn tick and not an immediate quarantine.
/// </summary>
[ConformanceSpec(
    "outbox.relay-unknown-route",
    "An unknown route reschedules quietly and delivers after the route is registered",
    Area = "Outbox",
    Contract = "A row toward an unregistered route reschedules quietly with a bumped failure count and delivers once the route is later registered.",
    Arrange = "A live source table holds one due row targeting a namespace and job not yet registered in the ledger.",
    Act = "The relay ticks before the route exists, a worker then registers it, the row is rewound to due, and the relay ticks again.",
    Assert = "The first tick throws nothing and leaves the row Pending with failure_count 1 and a future attempt, and the second tick delivers exactly one target job."
)]
public abstract class OutboxRelayRouteSpec<TFixture> : OutboxRelayIntegrationBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A row toward an unregistered route reschedules quietly, then delivers exactly once after the route is registered")]
    public async Task Unknown_route_reschedules_then_delivers_after_later_registration()
    {
        var ct = TestContext.Current.CancellationToken;
        var lateNs = TestKey("late");
        var dedup = TestKey("r6");
        var seed = EchoRow(dedup, jobNamespace: lateNs);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);

        var store = new HookedOutboxStore(SourceStore);

        // Tick 1: the route does not resolve. The ledger rejects it as a recoverable routing rejection
        // (below threshold), so the tick is quiet: no throw, and the row reschedules to Pending with a
        // bumped failure count and a future attempt instant. It is neither delivered nor quarantined.
        await Relay(store, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        var waiting = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
        Assert.True(waiting.Exists);
        Assert.Equal((byte)OutboxStatusCode.Pending, waiting.StatusCode);
        Assert.Equal(1, waiting.FailureCount);
        Assert.True(waiting.NextAttemptAtUtc > DateTime.UtcNow);

        // Register the route by initializing a worker for the late namespace against the same ledger.
        await using var late = BuildRuntimeFor(lateNs);
        var lateRuntime = late.GetServices<WorkerRuntime>().Single();
        await lateRuntime.InitializeAsync(ct);
        var lateNsId = lateRuntime.RegisteredNamespaceIds[lateNs];

        // Make the rescheduled row due again (its backoff pushed the attempt into the future).
        await Fixture.RewindOutboxAsync(SourceTable);

        // Tick 2: the route resolves. The row is delivered once and deleted.
        await Relay(store, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
        Assert.Equal(1, await CountLedgerJobsAsync(lateNsId, dedup, ct));
    }
}
