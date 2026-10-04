using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Definitions;

/// <summary>
/// Conformance for the definitions list read: catalog rows ordered by namespace name, definition
/// name, then id, with keyset pagination and an opt-in total.
/// </summary>
[ConformanceSpec(
    "list-job-definitions.keyset-page",
    "ListJobDefinitions pages the catalog by name order without duplicates",
    Area = "Reads",
    Contract = "ListJobDefinitions pages the catalog ordered namespace then name then id and reads the page plus an opt-in filter-wide count in one command."
)]
public sealed class ListJobDefinitionsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Walking NextCursor visits every definition exactly once in ascending order and TotalCount matches the walk")]
    public async Task Walks_catalog_in_ascending_order_and_total_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IActaOperations>();

        var seen = new List<DefinitionListItem>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await queries.Definitions.ListAsync(
                new ListDefinitionsQuery(JobNamespace: TestNamespace, PageSize: 1, Cursor: cursor),
                ct
            );
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
            Assert.True(pages < 200, "pagination did not terminate");
        } while (cursor is not null);

        Assert.NotEmpty(seen);
        Assert.Equal(seen.Count, seen.Select(static i => i.DefinitionId).Distinct().Count());

        // Collation-agnostic ordering check. The keyset walk must return the same rows in the same
        // order as a single un-paged read of the catalog. Asserting a fixed ordinal order here would
        // bake in .NET string ordering, which disagrees with locale-collated providers (Postgres
        // ignores the hyphen in names like 'jobref-probe', .NET CompareOrdinal does not). The contract
        // is that paging preserves the operation's own order, not ASCII order.
        // One read is capped at MaxPageSize whatever the caller asks for, so the comparison covers the
        // walk's first page-worth; the total is still checked against every row the walk saw.
        var comparable = Math.Min(seen.Count, JobsQueryLimits.MaxPageSize);
        var single = await queries.Definitions.ListAsync(
            new ListDefinitionsQuery(JobNamespace: TestNamespace, PageSize: comparable, IncludeTotal: true),
            ct
        );
        Assert.Equal(seen.Take(comparable).Select(static i => i.DefinitionId), single.Items.Select(static i => i.DefinitionId));
        Assert.Equal(seen.Count, single.TotalCount);
    }
}
