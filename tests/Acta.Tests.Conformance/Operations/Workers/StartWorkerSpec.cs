using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Workers;

/// <summary>
/// Operation-level conformance for <c>StartWorker</c>: namespace registration idempotency
/// (no churn when metadata is unchanged), append-only worker rows, and per-worker
/// <c>worker.started</c> event fields. Calls <c>StartWorker.Run</c> directly with a fresh unique
/// namespace per test so the registration is observed in isolation.
/// </summary>
[ConformanceSpec(
    "worker.start",
    "StartWorker writes the namespace on a metadata change and appends a worker row",
    Area = "Workers",
    Contract = "StartWorker writes the namespace only when its owner, description, or name is new, never its catalog hash, and appends one worker row and WorkerStarted event."
)]
public sealed class StartWorkerSpec : ActaStorageTestBase<ConformanceFixture>
{
    [Fact(
        DisplayName = "Namespace version is unchanged on same-metadata call and bumped on metadata change, and the start leaves the catalog hash unwritten"
    )]
    public async Task Namespace_upsert_is_idempotent_then_churns_on_metadata_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-ns");

        // Step 1: create namespace with ownerTeam=team-a
        var (nsId, _) = await WorkerTestOps.StartAsync(Services, nsName, "team-a", "desc", "host1", "v1", null, null, 1001, 4, ct);
        var ns1 = await Db.From<NamespaceEntity>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct);
        Assert.NotNull(ns1);
        Assert.Equal(0, ns1!.Version); // initial version pinned at 0
        var capturedModified = ns1.ModifiedAtUtc;
        Assert.Null(ns1.CatalogHash);

        // Step 2: same ownerTeam + description → no churn
        await WorkerTestOps.StartAsync(Services, nsName, "team-a", "desc", "host2", "v2", null, null, 1002, 4, ct);
        var ns2 = await Db.From<NamespaceEntity>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct);
        Assert.Equal(0, ns2!.Version); // version unchanged
        Assert.Equal(capturedModified, ns2.ModifiedAtUtc); // no write, timestamp unchanged

        // Step 3: different ownerTeam → version bumped by 1
        await WorkerTestOps.StartAsync(Services, nsName, "team-b", "desc", "host3", "v3", null, null, 1003, 4, ct);
        var ns3 = await Db.From<NamespaceEntity>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct);
        Assert.Equal(1, ns3!.Version); // bumped exactly once
        Assert.Equal("team-b", ns3.OwnerTeam);
        Assert.Null(ns3.CatalogHash);
    }

    [Fact(DisplayName = "Each StartWorker call returns a distinct worker id and leaves a distinct row in the namespace")]
    public async Task Worker_insert_is_append_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-aonly");

        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host1", "v1", null, null, 1001, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host2", "v1", null, null, 1002, 4, ct);
        var (_, wId3) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host3", "v1", null, null, 1003, 4, ct);

        Assert.NotEqual(wId1, wId2);
        Assert.NotEqual(wId2, wId3);
        Assert.NotEqual(wId1, wId3);

        var count = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == nsId).CountAsync(ct);
        Assert.Equal(3, count);
    }

    [Fact(DisplayName = "Each worker has exactly one WorkerStarted event with itself as the subject and no details")]
    public async Task Each_worker_has_exactly_one_WorkerStarted_event_with_itself_as_subject()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-evt");
        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host1", "v1", null, null, 1001, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host2", "v1", null, null, 1002, 4, ct);

        // The worker is the subject and the writer both; the event needs no actor to name it.
        foreach (var workerId in new[] { wId1, wId2 })
        {
            var started = Assert.Single(
                await Db.From<EventEntity>().Where(e => e.WorkerId == workerId && e.EventCode == EventCode.WorkerStarted).ToListAsync(ct)
            );
            Assert.Equal((EntityCode.Worker, (long)workerId), (started.EntityCode, started.EntityId));
            Assert.Null(started.Details);
            Assert.Equal(nsId, started.NamespaceId);
        }
    }

    [Fact(DisplayName = "Same host and process_id on two calls yields two distinct worker ids and two rows (no dedup)")]
    public async Task Duplicate_host_and_process_id_appends_two_distinct_workers()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-dup");

        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "same-host", "v1", null, null, 9999, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "same-host", "v1", null, null, 9999, 4, ct);

        Assert.NotEqual(wId1, wId2);

        var workers = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == nsId).ToListAsync(ct);
        Assert.Equal(2, workers.Count);
        Assert.Equal(2, workers.Select(w => w.Id).Distinct().Count());
    }

    [Fact(DisplayName = "Registering a worker/namespace named 'sys' is rejected while the seeded sys namespace remains intact")]
    public async Task Registering_a_worker_named_sys_is_rejected_and_the_seeded_namespace_survives()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            WorkerTestOps.StartAsync(Services, "sys", "team-a", "desc", "host1", "v1", null, null, 1001, 4, ct)
        );

        var page = await Services.GetRequiredService<INamespaces>().ListAsync(new ListNamespacesQuery(NameContains: "sys"), ct);
        Assert.Contains(page.Items, item => item.JobNamespace == "sys");
    }
}
