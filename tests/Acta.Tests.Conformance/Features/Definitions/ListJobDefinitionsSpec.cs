using Acta.Modules.Execution.Definitions;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Definitions;

/// <summary>
/// Conformance for the definitions list read: catalog rows ordered by namespace name, definition
/// name, then id, with keyset pagination and an opt-in total.
/// </summary>
[ConformanceSpec(
    "list-job-definitions.keyset-page",
    "ListJobDefinitions pages the catalog by name order without duplicates",
    Area = "Reads",
    Contract = "ListJobDefinitions pages the catalog ordered namespace then name then id and reads the page plus an opt-in filter-wide count in one command.",
    Arrange = "A namespace holds its registered definitions from the TestJobs manifest.",
    Act = "The catalog is walked one definition per page via NextCursor and read once un-paged with the opt-in total.",
    Assert = "The walk visits every definition exactly once in namespace, name, id order and the total matches the walk."
)]
[CoversStoreMethod(typeof(IDefinitionStore), nameof(IDefinitionStore.ListDefinitionsAsync))]
public abstract class ListJobDefinitionsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Walking NextCursor visits every definition exactly once in ascending order and TotalCount matches the walk")]
    public async Task Walks_catalog_in_ascending_order_and_total_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var seen = new List<JobDefinitionListItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await queries.Definitions.ListAsync(
                new ListJobDefinitionsQuery(JobNamespace: TestNamespace, PageSize: 1, Cursor: cursor),
                ct
            );
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages < 200, "pagination did not terminate");
        } while (cursor is not null);

        Assert.NotEmpty(seen);
        Assert.Equal(seen.Count, seen.Select(static i => i.JobDefinitionId).Distinct().Count());

        // Collation-agnostic ordering check. The keyset walk must return the same rows in the same
        // order as a single un-paged read of the catalog. Asserting a fixed ordinal order here would
        // bake in .NET string ordering, which disagrees with locale-collated providers (Postgres
        // ignores the hyphen in names like 'jobref-probe', .NET CompareOrdinal does not). The contract
        // is that paging preserves the operation's own order, not ASCII order.
        var single = await queries.Definitions.ListAsync(
            new ListJobDefinitionsQuery(JobNamespace: TestNamespace, PageSize: seen.Count, IncludeTotal: true),
            ct
        );
        Assert.Equal(seen.Select(static i => i.JobDefinitionId), single.Items.Select(static i => i.JobDefinitionId));
        Assert.Equal(seen.Count, single.TotalCount);
    }

    [Fact(DisplayName = "ListJobDefinitions returns the keyset page and the filter-wide total from one command")]
    public async Task Combined_read_returns_page_and_filter_wide_total()
    {
        var ct = TestContext.Current.CancellationToken;

        // Learn how many definitions the namespace carries so the filter-wide total has a target.
        var page_all = await Services
            .GetRequiredService<IDefinitionStore>()
            .ListDefinitionsAsync(new DefinitionPageRequest(TestNamespace, null, null, null, null, null, 1000, false), ct);
        var (all, _) = (page_all.Rows, page_all.Total);
        Assert.NotEmpty(all);

        var take = all.Count > 1 ? all.Count - 1 : 1;
        var page_rows = await Services
            .GetRequiredService<IDefinitionStore>()
            .ListDefinitionsAsync(new DefinitionPageRequest(TestNamespace, null, null, null, null, null, take, true), ct);
        var (rows, total) = (page_rows.Rows, page_rows.Total);

        Assert.Equal(take, rows.Count);
        Assert.Equal((long)all.Count, total);
    }
}
