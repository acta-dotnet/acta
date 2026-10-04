using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Namespaces;

/// <summary>Conformance for namespace update CAS: writes owner_team/description under a version CAS, clears on null, emits namespace.updated, rejects stale version, guards sys.</summary>
[ConformanceSpec(
    "update-namespace.cas",
    "Namespace update writes owner_team/description under a version CAS",
    Area = "Admin",
    Contract = "Update writes owner_team/description under a version CAS, clears fields on null, emits namespace.updated, and guards sys."
)]
public sealed class UpdateNamespaceSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private async Task<NamespaceEntity?> ReadNsAsync(CancellationToken ct) =>
        await Db.From<NamespaceEntity>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int nsId, CancellationToken ct) =>
        await Db.From<EventEntity>().Where(e => e.NamespaceId == nsId && e.EventCode == EventCode.NamespaceUpdated).CountAsync(ct);

    [Fact(DisplayName = "A matching version writes owner_team + description, bumps version, and emits namespace.updated")]
    public async Task Applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var v = (await ReadNsAsync(ct))!.Version;

        var outcome = await Operations.Namespaces.UpdateAsync(TestNamespace, v, "team-a", "new-desc", "edit", "op-1", ct);

        Assert.Equal(ControlAction.Applied, outcome.Action);
        var row = await ReadNsAsync(ct);
        Assert.Equal("team-a", row!.OwnerTeam);
        Assert.Equal("new-desc", row.Description);
        Assert.Equal(v + 1, row.Version);
        Assert.Equal(1, await EventCountAsync(nsId, ct));
    }

    [Fact(DisplayName = "A null field clears the column")]
    public async Task Null_clears()
    {
        var ct = TestContext.Current.CancellationToken;
        var v = (await ReadNsAsync(ct))!.Version;
        await Operations.Namespaces.UpdateAsync(TestNamespace, v, null, null, null, "op-1", ct);
        var row = await ReadNsAsync(ct);
        Assert.Null(row!.OwnerTeam);
        Assert.Null(row.Description);
    }

    [Fact(DisplayName = "A stale expected version is VersionConflict with the current version and no event")]
    public async Task Stale_version_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var v = (await ReadNsAsync(ct))!.Version;
        var outcome = await Operations.Namespaces.UpdateAsync(TestNamespace, v + 5, "x", null, null, "op-1", ct);
        Assert.Equal(ControlAction.VersionConflict, outcome.Action);
        Assert.Equal(v, outcome.Version);
        Assert.Equal(0, await EventCountAsync(nsId, ct));
    }

    [Fact(DisplayName = "Rejected sys updates leave the seeded row untouched and still listed")]
    public async Task Sys_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Db.From<NamespaceEntity>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operations.Namespaces.UpdateAsync("sys", 0, "x", null, null, null, ct)
        );

        var after = await Db.From<NamespaceEntity>().Where(n => n.Id == 1).SingleOrDefaultAsync(ct);
        Assert.Equal(before!.OwnerTeam, after!.OwnerTeam);
        Assert.Equal(before.Version, after.Version);

        var page = await Operations.Namespaces.ListNamesAsync(new ListNamespacesQuery(NameContains: "sys"), ct);
        Assert.Contains("sys", page.Items);
    }

    [Fact(DisplayName = "Overlong namespace fields is rejected before the store write")]
    public async Task Overlong_metadata_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operations.Namespaces.UpdateAsync(
                TestNamespace,
                0,
                new string('x', AdminTextLimits.NamespaceOwnerTeam + 1),
                null,
                null,
                null,
                ct
            )
        );
    }
}
