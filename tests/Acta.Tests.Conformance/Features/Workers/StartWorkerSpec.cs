using System.Globalization;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Namespaces;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Operation-level conformance for <c>StartWorker</c>: namespace upsert idempotency (hash-gated, no
/// churn when metadata is unchanged), append-only worker rows, and per-worker <c>worker.started</c>
/// event fields. Calls <c>StartWorker.Run</c> directly with a fresh unique namespace per test so the
/// upsert is observed in isolation.
/// </summary>
[ConformanceSpec(
    "worker.start",
    "StartWorker hash-gate-upserts namespace and appends a fresh worker row per call",
    Area = "Workers",
    Contract = "StartWorker hash-gate-upserts the namespace, always appends a fresh worker row, and emits exactly one WorkerStarted event per worker.",
    Arrange = "A fresh unique namespace isolates each StartWorker.Run call.",
    Act = "StartWorker runs repeatedly with unchanged metadata, changed metadata, and a duplicate worker identity.",
    Assert = "The namespace upsert is hash-gated with no churn on unchanged metadata, every call appends a fresh worker row, and each worker emits one WorkerStarted event."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.StartWorkerAsync))]
public abstract class StartWorkerSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Namespace version is unchanged on same-metadata call and bumped on metadata change")]
    public async Task Namespace_upsert_is_idempotent_then_churns_on_metadata_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = TestKey("sw-ns");

        // Step 1: create namespace with ownerTeam=team-a
        var (nsId, _) = await WorkerTestOps.StartAsync(Services, nsName, "team-a", "desc", "host1", "v1", null, null, 1001, 4, ct);
        var ns1 = await Db.From<JobNamespace>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct);
        Assert.NotNull(ns1);
        Assert.Equal(0, ns1!.Version); // initial version pinned at 0
        var capturedModified = ns1.ModifiedAtUtc;
        var capturedHash = ns1.CatalogHash;
        Assert.NotNull(capturedHash);

        // Step 2: same ownerTeam + description → same catalog_hash → no churn
        await WorkerTestOps.StartAsync(Services, nsName, "team-a", "desc", "host2", "v2", null, null, 1002, 4, ct);
        var ns2 = await Db.From<JobNamespace>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct);
        Assert.Equal(0, ns2!.Version); // version unchanged
        Assert.Equal(capturedModified, ns2.ModifiedAtUtc); // no write, timestamp unchanged
        Assert.Equal(capturedHash, ns2.CatalogHash); // hash unchanged

        // Step 3: different ownerTeam → different hash → version bumped by 1
        await WorkerTestOps.StartAsync(Services, nsName, "team-b", "desc", "host3", "v3", null, null, 1003, 4, ct);
        var ns3 = await Db.From<JobNamespace>().Where(n => n.Id == nsId).SingleOrDefaultAsync(ct);
        Assert.Equal(1, ns3!.Version); // bumped exactly once
        Assert.NotEqual(capturedHash, ns3.CatalogHash); // hash changed
    }

    [Fact(DisplayName = "Each StartWorker call returns a distinct worker id and leaves a distinct row in the namespace")]
    public async Task Worker_insert_is_append_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = TestKey("sw-aonly");

        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host1", "v1", null, null, 1001, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host2", "v1", null, null, 1002, 4, ct);
        var (_, wId3) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host3", "v1", null, null, 1003, 4, ct);

        Assert.NotEqual(wId1, wId2);
        Assert.NotEqual(wId2, wId3);
        Assert.NotEqual(wId1, wId3);

        var count = await Db.From<JobWorker>().Where(w => w.NamespaceId == nsId).CountAsync(ct);
        Assert.Equal(3, count);
    }

    [Fact(DisplayName = "Each worker has exactly one WorkerStarted event with actor worker and actor_key equal to the worker id")]
    public async Task Each_worker_has_exactly_one_WorkerStarted_event_with_correct_actor_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = TestKey("sw-evt");

        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host1", "v1", null, null, 1001, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host2", "v1", null, null, 1002, 4, ct);

        var events1 = await Db.From<JobEvent>().Where(e => e.WorkerId == wId1 && e.EventCode == JobEventCode.WorkerStarted).ToListAsync(ct);
        var e1 = Assert.Single(events1);
        Assert.Equal(JobActorCode.Worker, e1.ActorCode);
        Assert.Equal(wId1.ToString(CultureInfo.InvariantCulture), e1.ActorKey);
        Assert.Equal(nsId, e1.NamespaceId);

        var events2 = await Db.From<JobEvent>().Where(e => e.WorkerId == wId2 && e.EventCode == JobEventCode.WorkerStarted).ToListAsync(ct);
        var e2 = Assert.Single(events2);
        Assert.Equal(JobActorCode.Worker, e2.ActorCode);
        Assert.Equal(wId2.ToString(CultureInfo.InvariantCulture), e2.ActorKey);
        Assert.Equal(nsId, e2.NamespaceId);
    }

    [Fact(DisplayName = "Same host and process_id on two calls yields two distinct worker ids and two rows (no dedup)")]
    public async Task Duplicate_host_and_process_id_appends_two_distinct_workers()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = TestKey("sw-dup");

        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "same-host", "v1", null, null, 9999, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "same-host", "v1", null, null, 9999, 4, ct);

        Assert.NotEqual(wId1, wId2);

        var workers = await Db.From<JobWorker>().Where(w => w.NamespaceId == nsId).ToListAsync(ct);
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

        var page_rows = await Services
            .GetRequiredService<INamespaceStore>()
            .ListNamespacesAsync(new NamespacePageRequest("sys", null, null, 50, false), ct);
        var (rows, _) = (page_rows.Rows, page_rows.Total);
        Assert.Contains("sys", rows);
    }
}
