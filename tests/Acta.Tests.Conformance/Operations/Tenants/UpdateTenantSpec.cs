using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>Conformance for tenant update CAS: applies both fields with a version bump and a tenant.updated event, clears on null, rejects a stale version, NotFound for unknown keys.</summary>
[ConformanceSpec(
    "update-tenant.cas",
    "Tenant update is a version-CAS write that clears fields on null",
    Area = "Admin",
    Contract = "Update writes display_name/description under a version CAS, clears null fields, and emits tenant.updated to sys namespace 1."
)]
public sealed class UpdateTenantSpec : ActaStorageTestBase<ConformanceFixture>
{
    private async Task<TenantEntity?> ReadAsync(string key, CancellationToken ct) =>
        await Db.From<TenantEntity>().Where(t => t.TenantName == key).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int id, CancellationToken ct) =>
        await Db.From<EventEntity>().Where(e => e.TenantId == id && e.EventCode == EventCode.TenantUpdated).CountAsync(ct);

    [Fact(DisplayName = "A matching version writes both fields, bumps version, and emits tenant.updated")]
    public async Task Applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("adm-meta");
        var id = (await Services.GetRequiredService<ITenants>().RegisterAsync(key, "Old", "old-desc", ct)).TenantId;
        var v = (await ReadAsync(key, ct))!.Version;

        var outcome = await Services.GetRequiredService<ITenants>().UpdateAsync(key, v, "New", "new-desc", "edit", "op-1", ct);

        Assert.Equal(ControlAction.Applied, outcome.Action);
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
        var key = Unique("adm-meta-clear");
        await Services.GetRequiredService<ITenants>().RegisterAsync(key, "Old", "old-desc", ct);
        var v = (await ReadAsync(key, ct))!.Version;

        await Services.GetRequiredService<ITenants>().UpdateAsync(key, v, null, null, null, "op-1", ct);

        var row = await ReadAsync(key, ct);
        Assert.Null(row!.DisplayName);
        Assert.Null(row.Description);
    }

    [Fact(DisplayName = "A stale expected version is VersionConflict with the current version and no event")]
    public async Task Stale_version_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("adm-meta-cas");
        var id = (await Services.GetRequiredService<ITenants>().RegisterAsync(key, "Old", null, ct)).TenantId;
        var v = (await ReadAsync(key, ct))!.Version;

        var outcome = await Services.GetRequiredService<ITenants>().UpdateAsync(key, v + 5, "New", null, null, "op-1", ct);

        Assert.Equal(ControlAction.VersionConflict, outcome.Action);
        Assert.Equal(v, outcome.Version);
        Assert.Equal("Old", (await ReadAsync(key, ct))!.DisplayName);
        Assert.Equal(0, await EventCountAsync(id, ct));
    }

    [Fact(DisplayName = "An unknown key is NotFound")]
    public async Task Unknown_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await Services.GetRequiredService<ITenants>().UpdateAsync(Unique("adm-meta-ghost"), 0, "x", null, null, "op-1", ct);
        Assert.Equal(ControlAction.NotFound, outcome.Action);
    }

    [Fact(DisplayName = "Overlong tenant fields is rejected before the store write")]
    public async Task Overlong_metadata_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Services.GetRequiredService<ITenants>();
        var key = Unique("adm-meta-long");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateAsync(key, 0, new string('x', TextLimits.TenantDisplayName + 1), null, null, null, ct)
        );
    }
}
