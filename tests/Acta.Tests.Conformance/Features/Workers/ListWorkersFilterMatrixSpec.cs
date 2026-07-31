using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for <c>ListWorkers</c> filter dimensions: each filter partitions the worker result
/// set to exactly the matching rows and the opt-in total count applies the same filter as the row
/// query.
/// </summary>
[ConformanceSpec(
    "list-workers.filter-matrix",
    "ListWorkers filter-matrix selects exactly matching rows per dimension",
    Area = "Reads",
    Contract = "ListWorkers filters partition the worker rows to exactly the matching ids and exclude all non-matching ids for each filter dimension.",
    Arrange = "Worker rows are seeded per-test in isolation along the filtered dimension.",
    Act = "ListWorkers runs once per filter dimension with the opt-in total.",
    Assert = "The returned worker-id set equals exactly the matching ids with non-matching ids absent, and the total applies the same filter."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.ListWorkersAsync))]
public abstract class ListWorkersFilterMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Status filter partitions workers by status and each partition excludes all workers with different statuses")]
    public async Task Status_filter_partitions_workers_by_status()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var queries = Services.GetRequiredService<IActaOperations>();

        // Capture the single Active worker seeded by Runtime.InitializeAsync (only worker at this point)
        var wRuntime = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(wRuntime);
        var wRuntimeId = wRuntime!.Id;

        // W_stopped: start and immediately stop → Stopped
        var (_, wStoppedId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            null,
            "host-s",
            "v1",
            null,
            null,
            1001,
            1,
            ct
        );
        await Services.GetRequiredService<IWorkerStore>().StopWorkerAsync(ns, wStoppedId, ct);

        // W_dead: start, then direct-stamp Dead. Using the global MarkDeadWorkers reaper here is racy on
        // the shared parallel schema: a concurrent global sweep can reap this worker before our own call
        // runs, leaving our sweep with nothing (the `swept >= 1` assertion then flaked). Direct-stamping
        // the exact status is deterministic; this is a filter test, so how it became Dead is irrelevant.
        var (_, wDeadId) = await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-d", "v1", null, null, 1002, 1, ct);
        await Db.From<JobWorker>().Where(w => w.Id == wDeadId).UpdateOnlyAsync(() => new JobWorker { Status = WorkerStatusCode.Dead }, ct);

        // Expected sets: ids captured at seed time, independent of the filter under test
        var activeIds = new HashSet<int> { wRuntimeId };
        var stoppedIds = new HashSet<int> { wStoppedId };
        var deadIds = new HashSet<int> { wDeadId };

        // Force the Active worker back to Active + fresh right before the read. MarkDeadWorkers is a GLOBAL
        // sweep and other specs run it with a shorter dead-after window on the shared schema; a concurrent
        // sweep can flip this worker Active->Dead between InitializeAsync and our assertion. Resetting BOTH
        // the status (undo any sweep that already fired) and last_seen (block any sweep in the next window)
        // makes the Active partition deterministic.
        await Db.From<JobWorker>()
            .Where(w => w.Id == wRuntimeId)
            .UpdateOnlyAsync(() => new JobWorker { Status = WorkerStatusCode.Active, LastSeenAtUtc = DateTime.UtcNow }, ct);

        // Active filter: exact set + total, Stopped and Dead excluded
        var activePage = await queries.Workers.ListAsync(
            new ListWorkersQuery(JobNamespace: TestNamespace, Status: WorkerStatusCode.Active, IncludeTotal: true),
            ct
        );
        Assert.Equal(activeIds, [.. activePage.Items.Select(w => w.WorkerId)]);
        Assert.Equal(1L, activePage.TotalCount);
        Assert.Empty(activePage.Items.Select(w => w.WorkerId).Intersect(stoppedIds));
        Assert.Empty(activePage.Items.Select(w => w.WorkerId).Intersect(deadIds));

        // Stopped filter: exact set, Active and Dead excluded
        var stoppedPage = await queries.Workers.ListAsync(
            new ListWorkersQuery(JobNamespace: TestNamespace, Status: WorkerStatusCode.Stopped),
            ct
        );
        Assert.Equal(stoppedIds, [.. stoppedPage.Items.Select(w => w.WorkerId)]);
        Assert.Empty(stoppedPage.Items.Select(w => w.WorkerId).Intersect(activeIds));
        Assert.Empty(stoppedPage.Items.Select(w => w.WorkerId).Intersect(deadIds));

        // Dead filter: exact set, Active and Stopped excluded
        var deadPage = await queries.Workers.ListAsync(
            new ListWorkersQuery(JobNamespace: TestNamespace, Status: WorkerStatusCode.Dead),
            ct
        );
        Assert.Equal(deadIds, [.. deadPage.Items.Select(w => w.WorkerId)]);
        Assert.Empty(deadPage.Items.Select(w => w.WorkerId).Intersect(activeIds));
        Assert.Empty(deadPage.Items.Select(w => w.WorkerId).Intersect(stoppedIds));
    }

    [Fact(DisplayName = "JobNamespace filter scopes workers to exactly one namespace and excludes all other namespaces")]
    public async Task Namespace_filter_isolates_to_one_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var queries = Services.GetRequiredService<IActaOperations>();

        // Capture pre-existing workers in TestNamespace before seeding a second one
        var priorNs1Ids = (await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).ToListAsync(ct)).Select(w => w.Id).ToHashSet();

        // Add a second worker to TestNamespace so ns1 has 2 workers total
        var (_, w2Id) = await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-ns1-b", "v1", null, null, 2001, 1, ct);

        // Second namespace: one worker. StartWorker.Run upserts the namespace automatically
        var ns2Name = TestKey("ns2");
        var (_, w3Id) = await WorkerTestOps.StartAsync(Services, ns2Name, "test", null, "host-ns2", "v1", null, null, 2002, 1, ct);

        // Read each namespace independently
        var ns1Page = await queries.Workers.ListAsync(
            new ListWorkersQuery(JobNamespace: TestNamespace, PageSize: 100, IncludeTotal: true),
            ct
        );
        var ns2Page = await queries.Workers.ListAsync(new ListWorkersQuery(JobNamespace: ns2Name, PageSize: 100, IncludeTotal: true), ct);

        var ns1Ids = ns1Page.Items.Select(w => w.WorkerId).ToHashSet();
        var ns2Ids = ns2Page.Items.Select(w => w.WorkerId).ToHashSet();

        // ns2 has exactly the one worker we seeded
        Assert.Equal([w3Id], ns2Ids);
        Assert.Equal(1L, ns2Page.TotalCount);

        // ns1 has exactly the prior workers plus w2
        Assert.Equal(new HashSet<int>(priorNs1Ids) { w2Id }, ns1Ids);
        Assert.Equal((long)(priorNs1Ids.Count + 1), ns1Page.TotalCount);

        // Cross-exclusion: neither namespace bleeds into the other
        Assert.Empty(ns1Ids.Intersect(ns2Ids));
    }
}
