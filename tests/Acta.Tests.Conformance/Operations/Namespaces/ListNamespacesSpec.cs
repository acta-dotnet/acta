using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>
/// Conformance for <c>ListNamespaces</c>: the admin-list read that pages namespaces name-ascending
/// carrying status, owner team, description, and version, alongside the seeded sys row.
/// </summary>
[ConformanceSpec(
    "list-namespaces.admin-page",
    "ListNamespaces pages namespaces with status, fields, and version",
    Area = "Reads",
    Contract = "ListNamespaces pages namespaces name-ascending carrying id, status, owner_team, description, and version, and includes the seeded sys row."
)]
public sealed class ListNamespacesSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    // The name is this run's own, so filtering on it reaches the row in a database holding earlier runs.
    private async Task<ListNamespacesItem> FindAsync(string name, CancellationToken ct) =>
        (await Operations.Namespaces.ListAsync(new ListNamespacesQuery(NameContains: name), ct)).Items.Single(r => r.JobNamespace == name);

    [Fact(DisplayName = "The admin row carries the namespace id, status, owner team, description, and version")]
    public async Task Row_carries_status_fields_and_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Write distinct non-null owner_team/description and bump the version off its default so a broken
        // projection cannot pass by reading a stray null or a stray 0.
        var current = (await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == TestNamespace).SingleOrDefaultAsync(ct))!;
        var updated = await Operations.Namespaces.UpdateAsync(
            TestNamespace,
            current.Version,
            "platform-team",
            "namespace admin description",
            null,
            null,
            ct
        );
        Assert.NotNull(updated.Version);

        var row = await FindAsync(TestNamespace, ct);
        Assert.Equal(nsId, row.NamespaceId);
        Assert.Equal(TestNamespace, row.JobNamespace);
        Assert.Equal(NamespaceStatusCode.Active, row.Status);
        Assert.Equal("platform-team", row.OwnerTeam);
        Assert.Equal("namespace admin description", row.Description);
        Assert.Equal(updated.Version, row.Version);
    }

    [Fact(DisplayName = "The seeded sys namespace is present as id 1, name sys, status active")]
    public async Task Sys_row_is_present()
    {
        var ct = TestContext.Current.CancellationToken;

        var page = await Operations.Namespaces.ListAsync(new ListNamespacesQuery(NameContains: "sys", IncludeTotal: true), ct);
        var sys = page.Items.Single(r => r.JobNamespace == "sys");
        Assert.Equal(1, sys.NamespaceId);
        Assert.Equal(NamespaceStatusCode.Active, sys.Status);
        Assert.NotNull(page.TotalCount);
        Assert.True(page.TotalCount >= 1);
    }
}
