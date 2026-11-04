using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>
/// Conformance for the namespace point-read: <c>GetNamespace</c> returns the <c>ListNamespacesItem</c>
/// projection addressed by name in any casing, resolves a suspended namespace and the seeded sys row
/// (lookup is status-blind and reserves no name), and returns null when no row matches.
/// </summary>
[ConformanceSpec(
    "get-namespace.point-read",
    "GetNamespace reads a namespace by name",
    Area = "Catalog",
    Contract = "GetNamespace returns the GetNamespaceResponse for a known name in any casing and any status, the seeded sys row included, or null."
)]
public sealed class GetNamespaceSpec : ActaStorageTestBase<ConformanceFixture>
{
    private INamespaces Namespaces() => Services.GetRequiredService<INamespaces>();

    private GetNamespaceHandler GetNamespace() => Services.GetRequiredService<GetNamespaceHandler>();

    private async Task<int> VersionAsync(CancellationToken ct) =>
        (await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == TestNamespace).SingleOrDefaultAsync(ct))!.Version;

    [Fact(DisplayName = "A known name returns the populated row, field for field the list row")]
    public async Task Known_name_returns_the_list_row()
    {
        var ct = TestContext.Current.CancellationToken;
        // Distinct non-null fields and a bumped version, so a broken projection cannot pass on a stray null or 0.
        var updated = await Namespaces().UpdateAsync(TestNamespace, await VersionAsync(ct), "platform-team", "detail read", null, null, ct);

        var row = await GetNamespace().HandleAsync(new GetNamespaceQuery(TestNamespace), ct);
        var listed = (await Namespaces().ListAsync(new ListNamespacesQuery(NameContains: TestNamespace), ct)).Items.Single(r =>
            r.JobNamespace == TestNamespace
        );

        Assert.NotNull(row);
        Assert.Equal(TestNamespaceId, row!.NamespaceId);
        Assert.Equal(TestNamespace, row.JobNamespace);
        Assert.Equal(NamespaceStatusCode.Active, row.Status);
        Assert.Equal("platform-team", row.OwnerTeam);
        Assert.Equal("detail read", row.Description);
        Assert.Equal(updated.Version, row.Version);
        Assert.Equal(
            (listed.NamespaceId, listed.JobNamespace, listed.Status, listed.OwnerTeam, listed.Description, listed.Version),
            (row.NamespaceId, row.JobNamespace, row.Status, row.OwnerTeam, row.Description, row.Version)
        );
    }

    [Fact(DisplayName = "The name is folded: a padded, upper-cased name reads the same row")]
    public async Task Name_is_case_folded()
    {
        var ct = TestContext.Current.CancellationToken;

        var row = await Namespaces().GetAsync("  " + TestNamespace.ToUpperInvariant() + " ", ct);

        Assert.NotNull(row);
        Assert.Equal(TestNamespaceId, row!.NamespaceId);
        Assert.Equal(TestNamespace, row.JobNamespace);
    }

    [Fact(DisplayName = "A suspended namespace still resolves with status Suspended")]
    public async Task Suspended_namespace_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        await Namespaces().SuspendAsync(TestNamespace, null, null, ct);

        var row = await GetNamespace().HandleAsync(new GetNamespaceQuery(TestNamespace), ct);

        Assert.NotNull(row);
        Assert.Equal(TestNamespaceId, row!.NamespaceId);
        Assert.Equal(NamespaceStatusCode.Suspended, row.Status);
    }

    [Fact(DisplayName = "The seeded sys namespace resolves as id 1")]
    public async Task Sys_resolves()
    {
        var ct = TestContext.Current.CancellationToken;

        var row = await GetNamespace().HandleAsync(new GetNamespaceQuery(NameSyntax.ReservedSystemName), ct);

        Assert.NotNull(row);
        Assert.Equal(NameSyntax.SystemNamespaceId, row!.NamespaceId);
        Assert.Equal(NameSyntax.ReservedSystemName, row.JobNamespace);
    }

    [Fact(DisplayName = "An unknown name returns null")]
    public async Task Unknown_name_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;

        var row = await GetNamespace().HandleAsync(new GetNamespaceQuery(Unique("get-ns-ghost")), ct);

        Assert.Null(row);
    }

    [Fact(DisplayName = "A name that is not dotted kebab is rejected before any read")]
    public async Task Malformed_name_throws()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(async () => await Namespaces().GetAsync("bad key", ct));
    }
}
