using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// A worker start against a namespace that already exists must allocate no namespace id. PostgreSQL
/// evaluates an identity default before it detects an <c>ON CONFLICT</c>, and SQLite advances
/// <c>sqlite_sequence</c> before it detects one, so an upsert-shaped registration silently spent one
/// <c>namespaces.id</c> on every worker start - the id space drains at worker-restart rate rather
/// than at namespace-creation rate.
/// </summary>
/// <remarks>
/// Asserted as density rather than as a raw allocator reading, so the spec needs no provider-specific
/// probe and tolerates the namespaces other specs create in parallel against the shared schema: an id
/// that was handed out but carries no row is exactly a burned id. One other spec leaves such a hole
/// legitimately - <c>DbSessionWriteSpec</c> deletes the namespace row it inserted, once per run - so
/// the gate is that the restarts opened fewer holes than there were restarts, which a burning
/// registration cannot satisfy at any restart count while a stray deletion cannot breach.
/// </remarks>
[ConformanceSpec(
    "worker.start.id-allocation",
    "StartWorker allocates a namespace id only when it creates the namespace",
    Area = "Workers",
    Contract = "A worker start against an existing namespace allocates no namespace id, so the id space tracks namespaces created, not workers started.",
    Arrange = "One namespace is created by its first worker start.",
    Act = "Workers restart repeatedly against that namespace, then one worker starts a brand-new namespace.",
    Assert = "The id range spanned by the two namespaces carries a row for all but at most a stray id, so the restarts allocated nothing."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.StartWorkerAsync))]
public abstract class StartWorkerIdAllocationSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    // A burning registration opens one hole per restart, so this is both the signal and the margin:
    // eight holes to fail on, against the single stray deletion a full run is known to leave.
    private const int Restarts = 8;

    [Fact(DisplayName = "Restarting workers against an existing namespace allocates no namespace ids")]
    public async Task Repeated_starts_against_one_namespace_allocate_no_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = TestKey("sw-alloc-a");
        var second = TestKey("sw-alloc-b");

        var (firstId, _) = await WorkerTestOps.StartAsync(Services, first, "team-a", "desc", "host-0", "v1", null, null, 4000, 4, ct);

        // Unchanged metadata on purpose: the hash gate makes these pure no-op registrations, which is
        // the case the burning shape charged an id for.
        for (var i = 1; i <= Restarts; i++)
        {
            await WorkerTestOps.StartAsync(Services, first, "team-a", "desc", $"host-{i}", "v1", null, null, 4000 + i, 4, ct);
        }

        var (secondId, _) = await WorkerTestOps.StartAsync(Services, second, "team-a", "desc", "host-last", "v1", null, null, 4100, 4, ct);

        Assert.True(secondId > firstId, $"the second namespace got id {secondId}, at or below the first namespace's {firstId}");

        var span = secondId - firstId + 1;
        var occupied = await Db.From<JobNamespace>().Where(n => n.Id >= firstId && n.Id <= secondId).CountAsync(ct);
        var holes = span - occupied;
        Assert.True(
            holes < Restarts,
            $"ids {firstId}..{secondId} span {span} values but only {occupied} carry a namespace row: "
                + $"{holes} id(s) were allocated without creating a namespace, which is what {Restarts} "
                + "restarts of an existing namespace must never do"
        );
    }
}
