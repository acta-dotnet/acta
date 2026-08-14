using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for the workers list read: worker rows ordered most recently seen first with keyset
/// pagination and an opt-in total.
/// </summary>
[ConformanceSpec(
    "list-workers.keyset-page",
    "ListWorkers pages workers most recently seen first without duplicates",
    Area = "Reads",
    Contract = "ListWorkers pages worker rows newest seen first by cursor and reads the page plus an opt-in filter-wide count in one command.",
    Arrange = "Two more workers are started alongside the fixture's worker.",
    Act = "The worker list is walked one per page via NextCursor and read once with IncludeTotal.",
    Assert = "The walk visits every worker once in descending last-seen order and the page plus filter-wide total come from one command."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.ListWorkersAsync))]
public abstract class ListWorkersSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Walking NextCursor visits every worker once in descending last-seen order with a TotalCount matching the walk")]
    public async Task Walks_workers_in_descending_last_seen_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-b", "v2", "engine-b", ".NET test", 1002, 8, ct);
        await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-c", "v3", "engine-c", ".NET test", 1003, 16, ct);

        var seen = new List<WorkerListItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await queries.Workers.ListAsync(new ListWorkersQuery(JobNamespace: TestNamespace, PageSize: 1, Cursor: cursor), ct);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages < 50, "pagination did not terminate");
        } while (cursor is not null);

        Assert.True(seen.Count >= 3, $"expected at least 3 workers, saw {seen.Count}");
        Assert.Equal(seen.Count, seen.Select(static w => w.WorkerId).Distinct().Count());
        for (var i = 1; i < seen.Count; i++)
        {
            var ordered =
                seen[i].LastHeartbeatAtUtc < seen[i - 1].LastHeartbeatAtUtc
                || (seen[i].LastHeartbeatAtUtc == seen[i - 1].LastHeartbeatAtUtc && seen[i].WorkerId < seen[i - 1].WorkerId);
            Assert.True(ordered, "rows are not in last_seen DESC, id DESC order");
        }

        var withTotal = await queries.Workers.ListAsync(
            new ListWorkersQuery(JobNamespace: TestNamespace, PageSize: 1, IncludeTotal: true),
            ct
        );
        Assert.Equal(seen.Count, withTotal.TotalCount);
    }

    [Fact(DisplayName = "ListWorkers returns the keyset page and the filter-wide total from one command")]
    public async Task Combined_read_returns_page_and_filter_wide_total()
    {
        var ct = TestContext.Current.CancellationToken;

        await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-b", "v2", "engine-b", ".NET test", 1002, 8, ct);
        await WorkerTestOps.StartAsync(Services, TestNamespace, "test", null, "host-c", "v3", "engine-c", ".NET test", 1003, 16, ct);

        var page = await Services
            .GetRequiredService<IWorkerStore>()
            .ListWorkersAsync(new WorkerPageRequest(TestNamespace, null, null, null, Take: 2, IncludeTotal: true), ct);
        var (rows, total) = (page.Rows, page.Total);

        Assert.Equal(2, rows.Count);
        Assert.True(total >= 3, $"expected filter-wide total of at least 3, saw {total}");

        var newest = rows[0];
        Assert.Equal("engine-c", newest.EngineVersion);
        Assert.Equal(".NET test", newest.DotnetVersion);
        Assert.Equal(1003, newest.ProcessId);
        Assert.Equal(16, newest.MaxConcurrency);

        for (var i = 1; i < rows.Count; i++)
        {
            var earlier = rows[i - 1];
            var current = rows[i];
            Assert.True(
                current.LastHeartbeatAtUtc < earlier.LastHeartbeatAtUtc
                    || (current.LastHeartbeatAtUtc == earlier.LastHeartbeatAtUtc && current.WorkerId < earlier.WorkerId),
                "combined page rows are not in last_seen_at_utc DESC, id DESC order"
            );
        }
    }
}
