using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Storage;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Workers;

/// <summary>
/// A development restart - the deploy and the worker start - against a namespace that already exists must allocate no
/// namespace id. PostgreSQL evaluates an identity default before it detects an <c>ON CONFLICT</c>, and SQLite advances
/// <c>sqlite_sequence</c> before it detects one, so an upsert-shaped namespace insert would silently spend one
/// <c>namespaces.namespace_id</c> on every restart - the id space would drain at restart rate rather
/// than at namespace-creation rate.
/// </summary>
/// <remarks>
/// Asserted as density rather than as a raw allocator reading, so the spec needs no provider-specific
/// probe: an id that was handed out but carries no row is exactly a burned id. On the servers the class
/// runs on a schema of its own, because other specs burn ids legitimately in the shared one: a first
/// deploy that rolls back, and each first deploy of one name that loses a race, spends an identity value.
/// On SQLite the class shares the file, where a rolled-back insert spends nothing and one spec leaves a
/// hole - <c>DbSessionWriteSpec</c> deletes the namespace row it inserted, once per run - so the gate is
/// that the restarts opened fewer holes than there were restarts, which a burning insert cannot satisfy
/// at any restart count while a stray deletion cannot breach.
/// </remarks>
[ConformanceSpec(
    "worker.start.id-allocation",
    "A deploy allocates a namespace id only when it creates the namespace",
    Area = "Workers",
    Contract = "A deploy and a worker start against an existing namespace allocate no namespace id, so the id space tracks namespaces created, not restarts."
)]
public sealed class StartWorkerIdAllocationSpec : ActaStorageTestBase<ConformanceFixture>
{
    // A burning namespace insert opens one hole per restart, so this is both the signal and the margin:
    // eight holes to fail on, against the single stray deletion a full run is known to leave.
    private const int Restarts = 8;

    protected override bool OwnSchema => true;

    [Fact(DisplayName = "Restarting workers against an existing namespace allocates no namespace ids")]
    public async Task Repeated_starts_against_one_namespace_allocate_no_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Unique("sw-alloc-a");
        var second = Unique("sw-alloc-b");

        var (firstId, _) = await WorkerTestOps.StartAsync(Services, first, "team-a", "desc", "host-0", "v1", null, null, 4000, 4, ct);

        // Unchanged metadata on purpose: these restarts write no namespace row, which is the case a
        // burning shape charges an id for.
        for (var i = 1; i <= Restarts; i++)
        {
            await WorkerTestOps.StartAsync(Services, first, "team-a", "desc", $"host-{i}", "v1", null, null, 4000 + i, 4, ct);
        }

        var (secondId, _) = await WorkerTestOps.StartAsync(Services, second, "team-a", "desc", "host-last", "v1", null, null, 4100, 4, ct);

        Assert.True(secondId > firstId, $"the second namespace got id {secondId}, at or below the first namespace's {firstId}");

        var span = secondId - firstId + 1;
        var occupied = await Db.From<NamespaceEntity>().Where(n => n.Id >= firstId && n.Id <= secondId).CountAsync(ct);
        var holes = span - occupied;
        Assert.True(
            holes < Restarts,
            $"ids {firstId}..{secondId} span {span} values but only {occupied} carry a namespace row: "
                + $"{holes} id(s) were allocated without creating a namespace, which is what {Restarts} "
                + "restarts of an existing namespace must never do"
        );
    }
}
