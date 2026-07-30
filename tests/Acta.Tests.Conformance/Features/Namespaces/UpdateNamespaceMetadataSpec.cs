using Acta;
using Acta.Kernel;
using Acta.Modules.Execution.Jobs;
using Acta.Modules.Execution.Namespaces;
using Acta.Modules.Execution.Tenants;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Namespaces;

/// <summary>Conformance for namespace metadata CAS: writes owner_team/description under a version CAS, clears on null, emits namespace.metadata-changed, rejects stale version, guards sys.</summary>
[ConformanceSpec(
    "update-namespace-metadata.cas",
    "Namespace metadata update writes owner_team/description under a version CAS",
    Area = "Admin",
    Contract = "Metadata update writes owner_team/description under a version CAS, clears fields on null, emits namespace.metadata-changed, and guards sys.",
    Arrange = "The worker registers the test namespace with a known version.",
    Act = "Metadata is updated with the current version, with null fields, with a stale version, and sys is attempted through the facade.",
    Assert = "A match updates, bumps, and emits namespace.metadata-changed, null clears, stale conflicts without an event, and sys is rejected."
)]
[CoversStoreMethod(typeof(INamespaceStore), nameof(INamespaceStore.UpdateNamespaceMetadataAsync))]
public abstract class UpdateNamespaceMetadataSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    private static JobControlActor Actor() => new(JobActorCode.Operator, "op-1");

    private async Task<JobNamespace?> ReadNsAsync(CancellationToken ct) =>
        await Db.From<JobNamespace>().Where(n => n.Name == TestNamespace).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(short nsId, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.NamespaceId == nsId && e.EventCode == JobEventCode.NamespaceMetadataChanged).CountAsync(ct);

    [Fact(DisplayName = "A matching version writes owner_team + description, bumps version, and emits namespace.metadata-changed")]
    public async Task Applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var v = (await ReadNsAsync(ct))!.Version;

        var outcome = await Services
            .GetRequiredService<INamespaceStore>()
            .UpdateNamespaceMetadataAsync(new UpdateNamespaceMetadataCommand(TestNamespace, "team-a", "new-desc", v, Actor(), "edit"), ct);

        Assert.Equal(AdminControlAction.Applied, outcome.Action);
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
        await Services
            .GetRequiredService<INamespaceStore>()
            .UpdateNamespaceMetadataAsync(new UpdateNamespaceMetadataCommand(TestNamespace, null, null, v, Actor(), null), ct);
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
        var outcome = await Services
            .GetRequiredService<INamespaceStore>()
            .UpdateNamespaceMetadataAsync(new UpdateNamespaceMetadataCommand(TestNamespace, "x", null, v + 5, Actor(), null), ct);
        Assert.Equal(AdminControlAction.VersionConflict, outcome.Action);
        Assert.Equal(v, outcome.Version);
        Assert.Equal(0, await EventCountAsync(nsId, ct));
    }

    [Fact(DisplayName = "Rejected sys metadata edits leave the seeded row untouched and still listed")]
    public async Task Sys_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = await Db.From<JobNamespace>().Where(n => n.Id == (short)1).SingleOrDefaultAsync(ct);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operations.Namespaces.UpdateMetadataAsync("sys", "x", null, 0, null, null, ct)
        );

        var after = await Db.From<JobNamespace>().Where(n => n.Id == (short)1).SingleOrDefaultAsync(ct);
        Assert.Equal(before!.OwnerTeam, after!.OwnerTeam);
        Assert.Equal(before.Version, after.Version);

        var page = await Operations.Namespaces.ListAsync(new ListNamespacesQuery(NameContains: "sys"), ct);
        Assert.Contains("sys", page.Items);
    }

    [Fact(DisplayName = "Overlong namespace metadata is rejected before the store write")]
    public async Task Overlong_metadata_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Operations.Namespaces.UpdateMetadataAsync(
                TestNamespace,
                new string('x', CatalogMetadataLimits.NamespaceOwnerTeam + 1),
                null,
                0,
                null,
                null,
                ct
            )
        );
    }
}
