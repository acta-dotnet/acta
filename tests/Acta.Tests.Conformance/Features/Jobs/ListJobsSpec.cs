using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for the jobs list read: newest-first keyset pagination over <c>job</c> with
/// namespace/name filters, an opt-in filter-wide total, and no payload exposure.
/// </summary>
[ConformanceSpec(
    "list-jobs.keyset-page",
    "ListJobs pages newest first by keyset cursor without duplicates",
    Area = "Reads",
    Contract = "ListJobs pages newest first by cursor without duplicates and reads the page plus an opt-in filter-wide count in one command.",
    Arrange = "Five jobs are enqueued in the test namespace.",
    Act = "The jobs are paged two per page via NextCursor and the list is read with and without IncludeTotal.",
    Assert = "Pages arrive newest first without duplicates, the page plus filter-wide count come from one trip, and no item exposes a payload."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ListJobsAsync))]
public abstract class ListJobsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private async Task<IReadOnlyList<long>> SeedJobsForList(int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var rows = new List<JobEnqueueRow>(count);
        for (var i = 0; i < count; i++)
        {
            var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(i, i));
            rows.Add(new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload));
        }

        var results = await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct);
        return results.Select(static r => r.JobId).ToList();
    }

    [Fact(
        DisplayName = "Walking NextCursor visits every job once in descending order, with HasMore false and NextCursor null on the final page"
    )]
    public async Task Walks_all_pages_in_descending_order_without_duplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = (await SeedJobsForList(5)).ToHashSet();
        var queries = Services.GetRequiredService<IActaOperations>();

        var seen = new List<JobListItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await queries.Ledger.ListJobsAsync(
                new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", PageSize: 2, Cursor: cursor),
                ct
            );
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor);
            }
            pages++;
            Assert.True(pages < 20, "pagination did not terminate");
        } while (cursor is not null);

        Assert.Equal(seen.Count, seen.Select(static i => i.JobId).Distinct().Count());
        Assert.True(enqueued.SetEquals(seen.Select(static i => i.JobId)));
        for (var i = 1; i < seen.Count; i++)
        {
            var earlier = seen[i - 1];
            var current = seen[i];
            Assert.True(
                current.CreatedAtUtc < earlier.CreatedAtUtc
                    || (current.CreatedAtUtc == earlier.CreatedAtUtc && current.JobId < earlier.JobId),
                "rows are not in created_at_utc DESC, id DESC order"
            );
        }
    }

    [Fact(DisplayName = "TotalCount is null unless IncludeTotal is set and is filter-wide when requested")]
    public async Task Total_is_filter_wide_and_opt_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedJobsForList(3);
        var queries = Services.GetRequiredService<IActaOperations>();

        var withoutTotal = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", PageSize: 1),
            ct
        );
        Assert.Null(withoutTotal.TotalCount);

        var withTotal = await queries.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, JobName: "add-numbers", PageSize: 1, IncludeTotal: true),
            ct
        );
        Assert.Single(withTotal.Items);
        Assert.Equal(3, withTotal.TotalCount);
    }

    [Fact(DisplayName = "ListJobs returns the keyset page and the filter-wide total from one command")]
    public async Task Combined_read_returns_page_and_filter_wide_total()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedJobsForList(3);

        var page_total = await Services
            .GetRequiredService<IJobStore>()
            .ListJobsAsync(
                new JobPageRequest(TestNamespace, null, "add-numbers", null, null, null, null, null, null, null, null, null, 2, true),
                ct
            );
        var (rows, total) = (page_total.Rows, page_total.Total);

        Assert.Equal(2, rows.Count);
        Assert.Equal(3L, total);
        for (var i = 1; i < rows.Count; i++)
        {
            var earlier = rows[i - 1];
            var current = rows[i];
            Assert.True(
                current.CreatedAtUtc < earlier.CreatedAtUtc
                    || (current.CreatedAtUtc == earlier.CreatedAtUtc && current.JobId < earlier.JobId),
                "combined page rows are not in created_at_utc DESC, id DESC order"
            );
        }
    }

    [Fact(DisplayName = "The list projection exposes no payload column")]
    public void Job_list_item_exposes_no_payload()
    {
        var names = typeof(JobListItem).GetProperties().Select(static p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain("input", names);
        Assert.DoesNotContain("inputformatid", names);
        Assert.DoesNotContain("payload", names);
    }
}
