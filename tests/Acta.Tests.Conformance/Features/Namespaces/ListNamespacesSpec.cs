using Acta.Features.Namespaces;
using Acta.Features.Shared;
using Acta.Features.Tenants;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Namespaces;

/// <summary>
/// Conformance for <c>ListNamespaces</c>: a keyset page of namespace names ordered name
/// ascending, with an optional prefix filter and an opt-in filter-wide total, all in one round trip.
/// </summary>
[ConformanceSpec(
    "list-namespaces.keyset-page",
    "ListNamespaces pages namespaces name-ascending with an opt-in total",
    Area = "Reads",
    Contract = "ListNamespaces pages namespace names ascending without duplicates and reads the page plus an opt-in filter-wide count in one command.",
    Arrange = "The fixture's test namespace is registered.",
    Act = "Namespaces are paged by cursor, filtered by prefix, and read with and without IncludeTotal.",
    Assert = "The walk visits the registered namespace once with no duplicates, the prefix filter scopes the rows, and the opt-in total arrives with the page."
)]
[CoversStoreMethod(typeof(INamespaceStore), nameof(INamespaceStore.ListNamespacesAsync))]
public abstract class ListNamespacesSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
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
            var page_rows = await Services
                .GetRequiredService<INamespaceStore>()
                .ListNamespacesAsync(new NamespacePageRequest(null, null, cursor, 50, false), ct);
            var (rows, _) = (page_rows.Rows, page_rows.Total);
            seen.AddRange(rows);
            cursor = rows.Count == 50 ? rows[^1] : null;
            Assert.True(++pages < 100_000, "pagination did not terminate");
        } while (cursor is not null);

        Assert.Contains(TestNamespace, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact(DisplayName = "A name filter narrows to the matching namespace and IncludeTotal returns its prefix-wide count")]
    public async Task Total_is_prefix_wide_and_opt_in()
    {
        var ct = TestContext.Current.CancellationToken;

        var page__ = await Services
            .GetRequiredService<INamespaceStore>()
            .ListNamespacesAsync(new NamespacePageRequest(null, null, null, 1, false), ct);
        var (_, noTotal) = (page__.Rows, page__.Total);
        Assert.Null(noTotal);

        // No '%' is an exact LIKE, so the pattern matches only the fixture namespace: one row, total one.
        var page_rows = await Services
            .GetRequiredService<INamespaceStore>()
            .ListNamespacesAsync(new NamespacePageRequest(TestNamespace, null, null, 50, true), ct);
        var (rows, total) = (page_rows.Rows, page_rows.Total);
        Assert.Equal([TestNamespace], rows);
        Assert.Equal(1L, total);
    }
}
