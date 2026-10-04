using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>Conformance for tenant suspend: idempotent status flip, version bump, one 15xx event to the sys namespace, and NotFound for unknown keys.</summary>
[ConformanceSpec(
    "suspend-tenant.status-flip",
    "Tenant suspend flips status and emits one 15xx event to sys namespace",
    Area = "Admin",
    Contract = "Suspend flips a tenant to Suspended once with a version bump and one tenant.suspended to sys namespace 1, and an unknown key is NotFound."
)]
public sealed class SuspendTenantSpec : ActaStorageTestBase<ConformanceFixture>
{
    private async Task<Tenant?> ReadTenantAsync(string key, CancellationToken ct) =>
        await Db.From<Tenant>().Where(t => t.TenantKey == key).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int tenantId, EventCode code, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.TenantId == tenantId && e.EventCode == code).CountAsync(ct);

    [Fact(DisplayName = "Suspending an active tenant applies, bumps version, and emits one tenant.suspended to sys")]
    public async Task Suspend_applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-suspend");
        var id = (await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct)).TenantId;
        var before = await ReadTenantAsync(key, ct);

        var outcome = await Services.GetRequiredService<ITenants>().SuspendAsync(key, "hold", "op-1", ct);

        Assert.Equal(ControlAction.Applied, outcome.Action);
        var after = await ReadTenantAsync(key, ct);
        Assert.Equal(TenantStatusCode.Suspended, after!.Status);
        Assert.True(after.Version > before!.Version);
        Assert.Equal(after.Version, outcome.Version);
        Assert.Equal(1, await EventCountAsync(id, EventCode.TenantSuspended, ct));
        var ev = await Db.From<JobEvent>()
            .Where(e => e.TenantId == id && e.EventCode == EventCode.TenantSuspended)
            .SingleOrDefaultAsync(ct);
        Assert.Equal(1, ev!.NamespaceId);
        Assert.Equal("op-1", ev.ActorKey);
        Assert.Equal("hold", ev.ReasonMessage);
    }

    [Fact(DisplayName = "Re-suspending is AlreadyInState with no second event")]
    public async Task Suspend_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-suspend-idem");
        var id = (await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct)).TenantId;
        await Services.GetRequiredService<ITenants>().SuspendAsync(key, null, "op-1", ct);

        var again = await Services.GetRequiredService<ITenants>().SuspendAsync(key, null, "op-1", ct);

        Assert.Equal(ControlAction.AlreadyInState, again.Action);
        Assert.Equal(1, await EventCountAsync(id, EventCode.TenantSuspended, ct));
    }

    [Fact(DisplayName = "Suspending an unknown key is NotFound")]
    public async Task Unknown_key_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await Services.GetRequiredService<ITenants>().SuspendAsync(TestKey("adm-ghost"), null, "op-1", ct);
        Assert.Equal(ControlAction.NotFound, outcome.Action);
        Assert.Null(outcome.Version);
    }
}
