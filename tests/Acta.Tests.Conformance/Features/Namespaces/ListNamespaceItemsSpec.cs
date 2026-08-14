using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Namespaces;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Namespaces;

/// <summary>
/// Conformance for <c>ListNamespaceItems</c>: the admin-list read that pages namespaces name-ascending
/// carrying status, owner team, description, and version, alongside the seeded sys row.
/// </summary>
[ConformanceSpec(
    "list-namespace-items.admin-page",
    "ListNamespaceItems pages namespaces with status, fields, and version",
    Area = "Reads",
    Contract = "ListNamespaceItems pages namespaces name-ascending carrying id, status, owner_team, description, and version, and includes the seeded sys row.",
    Arrange = "The worker registers the test namespace and its owner team, description, and version are set to distinct non-null values.",
    Act = "Namespaces are paged by cursor to reach the test row and the sys prefix is read.",
    Assert = "The test row carries the distinct owner_team, description, id, and bumped version, and the sys row is present as id 1 name sys status active."
)]
[CoversStoreMethod(typeof(INamespaceStore), nameof(INamespaceStore.ListNamespaceItemsAsync))]
public abstract class ListNamespaceItemsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    private async Task<NamespaceListItem?> FindAsync(string name, System.Threading.CancellationToken ct)
    {
        var store = Services.GetRequiredService<INamespaceStore>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await store.ListNamespaceItemsAsync(new NamespacePageRequest(null, null, cursor, 50, false), ct);
            var hit = page.Rows.FirstOrDefault(r => r.JobNamespace == name);
            if (hit is not null)
            {
                return hit;
            }
            cursor = page.Rows.Count == 50 ? page.Rows[^1].JobNamespace : null;
            Assert.True(++pages < 100_000, "pagination did not terminate");
        } while (cursor is not null);
        return null;
    }

    [Fact(DisplayName = "The admin row carries the namespace id, status, owner team, description, and version")]
    public async Task Row_carries_status_fields_and_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];

        // Write distinct non-null owner_team/description and bump the version off its default so a broken
        // positional projection cannot pass by reading a stray null or a stray 0.
        var current = (await Db.From<JobNamespace>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct))!;
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
        Assert.NotNull(row);
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
        var store = Services.GetRequiredService<INamespaceStore>();

        var page = await store.ListNamespaceItemsAsync(new NamespacePageRequest("sys", null, null, 50, true), ct);
        var sys = page.Rows.Single(r => r.JobNamespace == "sys");
        Assert.Equal((short)1, sys.NamespaceId);
        Assert.Equal(NamespaceStatusCode.Active, sys.Status);
        Assert.NotNull(page.Total);
        Assert.True(page.Total >= 1);
    }
}
