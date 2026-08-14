using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Tenants;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Tenants;

/// <summary>Conformance for tenant update CAS: applies both fields with a version bump and a tenant.updated event, clears on null, rejects a stale version, NotFound for unknown keys.</summary>
[ConformanceSpec(
    "update-tenant.cas",
    "Tenant update is a version-CAS write that clears fields on null",
    Area = "Admin",
    Contract = "Update writes display_name/description under a version CAS, clears null fields, and emits tenant.updated to sys namespace 1.",
    Arrange = "An active tenant with a known version is registered.",
    Act = "Fields are updated with the current version, with null fields, with a stale version, and against an unknown key.",
    Assert = "A match updates, bumps, and emits tenant.updated, null clears, stale conflicts, and unknown keys report NotFound."
)]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.UpdateTenantAsync))]
public abstract class UpdateTenantSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static JobControlActor Actor() => new(ActorCode.Operator, "op-1");

    private async Task<Tenant?> ReadAsync(string key, CancellationToken ct) =>
        await Db.From<Tenant>().Where(t => t.TenantKey == key).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int id, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.TenantId == id && e.EventCode == EventCode.TenantUpdated).CountAsync(ct);

    [Fact(DisplayName = "A matching version writes both fields, bumps version, and emits tenant.updated")]
    public async Task Applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-meta");
        var id = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, "Old", "old-desc", ct);
        var v = (await ReadAsync(key, ct))!.Version;

        var outcome = await Services
            .GetRequiredService<ITenantStore>()
            .UpdateTenantAsync(new UpdateTenantCommand(key, "New", "new-desc", v, Actor(), "edit"), ct);

        Assert.Equal(AdminControlAction.Applied, outcome.Action);
        var row = await ReadAsync(key, ct);
        Assert.Equal("New", row!.DisplayName);
        Assert.Equal("new-desc", row.Description);
        Assert.Equal(v + 1, row.Version);
        Assert.Equal(row.Version, outcome.Version);
        Assert.Equal(1, await EventCountAsync(id, ct));
    }

    [Fact(DisplayName = "A null field clears the column")]
    public async Task Null_clears()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-meta-clear");
        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, "Old", "old-desc", ct);
        var v = (await ReadAsync(key, ct))!.Version;

        await Services.GetRequiredService<ITenantStore>().UpdateTenantAsync(new UpdateTenantCommand(key, null, null, v, Actor(), null), ct);

        var row = await ReadAsync(key, ct);
        Assert.Null(row!.DisplayName);
        Assert.Null(row.Description);
    }

    [Fact(DisplayName = "A stale expected version is VersionConflict with the current version and no event")]
    public async Task Stale_version_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-meta-cas");
        var id = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, "Old", null, ct);
        var v = (await ReadAsync(key, ct))!.Version;

        var outcome = await Services
            .GetRequiredService<ITenantStore>()
            .UpdateTenantAsync(new UpdateTenantCommand(key, "New", null, v + 5, Actor(), null), ct);

        Assert.Equal(AdminControlAction.VersionConflict, outcome.Action);
        Assert.Equal(v, outcome.Version);
        Assert.Equal("Old", (await ReadAsync(key, ct))!.DisplayName);
        Assert.Equal(0, await EventCountAsync(id, ct));
    }

    [Fact(DisplayName = "An unknown key is NotFound")]
    public async Task Unknown_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await Services
            .GetRequiredService<ITenantStore>()
            .UpdateTenantAsync(new UpdateTenantCommand(TestKey("adm-meta-ghost"), "x", null, 0, Actor(), null), ct);
        Assert.Equal(AdminControlAction.NotFound, outcome.Action);
    }

    [Fact(DisplayName = "Overlong tenant fields is rejected before the store write")]
    public async Task Overlong_metadata_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Services.GetRequiredService<TenantsService>();
        var key = TestKey("adm-meta-long");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateAsync(key, new string('x', CatalogLimits.TenantDisplayName + 1), null, 0, null, null, ct)
        );
    }
}
