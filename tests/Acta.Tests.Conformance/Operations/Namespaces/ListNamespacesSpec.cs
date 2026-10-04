using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>
/// Conformance for <c>ListNamespaces</c>: a keyset page of namespace names ordered name
/// ascending, with an optional name filter and an opt-in filter-wide total, all in one round trip.
/// </summary>
[ConformanceSpec(
    "list-namespaces.keyset-page",
    "ListNamespaces pages namespaces name-ascending with an opt-in total",
    Area = "Reads",
    Contract = "ListNamespaces pages namespace names ascending without duplicates and reads the page plus an opt-in filter-wide count in one command."
)]
public sealed class ListNamespacesSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Walking the cursor visits the registered TestNamespace once with no duplicates")]
    public async Task Contains_registered_namespace_with_no_duplicates()
    {
        var ct = TestContext.Current.CancellationToken;

        // Keyset paging is self-consistent under the database's own collation (the cursor predicate and
        // ORDER BY share it), so we assert coverage and uniqueness rather than a client-side string order.
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Operations.Namespaces.ListNamesAsync(new ListNamespacesQuery(PageSize: 50, Cursor: cursor), ct);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(++pages < 100_000, "pagination did not terminate");
        } while (cursor is not null);

        Assert.Contains(TestNamespace, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "A name filter narrows to the matching namespaces and IncludeTotal counts them")]
    public async Task Total_counts_the_filtered_names_and_is_opt_in()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null((await Operations.Namespaces.ListNamesAsync(new ListNamespacesQuery(PageSize: 1), ct)).TotalCount);

        // The fixture namespace is this run's own name, so only names containing it can match.
        var page = await Operations.Namespaces.ListNamesAsync(new ListNamespacesQuery(NameContains: TestNamespace, IncludeTotal: true), ct);
        Assert.Contains(TestNamespace, page.Items);
        Assert.All(page.Items, name => Assert.Contains(TestNamespace, name, StringComparison.Ordinal));
        Assert.Equal(page.Items.Count, page.TotalCount);
    }
}
