using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>Conformance for tenant resume: the status flips back with one 15xx event, and resuming an active tenant writes nothing.</summary>
[ConformanceSpec(
    "resume-tenant.status-flip",
    "Tenant resume flips status back and emits one 15xx event to sys namespace",
    Area = "Admin",
    Contract = "Resume flips a suspended tenant back to Active with one tenant.resumed to sys namespace 1, and resuming an active tenant writes nothing."
)]
public sealed class ResumeTenantSpec : ActaStorageTestBase<ConformanceFixture>
{
    private async Task<TenantEntity?> ReadTenantAsync(string key, CancellationToken ct) =>
        await Db.From<TenantEntity>().Where(t => t.TenantName == key).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int tenantId, EventCode code, CancellationToken ct) =>
        await Db.From<EventEntity>().Where(e => e.TenantId == tenantId && e.EventCode == code).CountAsync(ct);

    [Fact(DisplayName = "Resuming a suspended tenant applies and emits one tenant.resumed")]
    public async Task Resume_applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("adm-resume");
        var id = (await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct)).TenantId;
        await Services.GetRequiredService<ITenants>().SuspendAsync(key, null, "op-1", ct);

        var outcome = await Services.GetRequiredService<ITenants>().ResumeAsync(key, "back", "op-1", ct);

        Assert.Equal(ControlAction.Applied, outcome.Action);
        Assert.Equal(TenantStatusCode.Active, (await ReadTenantAsync(key, ct))!.Status);
        Assert.Equal(1, await EventCountAsync(id, EventCode.TenantResumed, ct));
    }

    [Fact(DisplayName = "Re-resuming an active tenant is AlreadyInState with no event")]
    public async Task Resume_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("adm-resume-idem");
        var id = (await Services.GetRequiredService<ITenants>().RegisterAsync(key, null, null, ct)).TenantId;

        var outcome = await Services.GetRequiredService<ITenants>().ResumeAsync(key, null, "op-1", ct);

        Assert.Equal(ControlAction.AlreadyInState, outcome.Action);
        Assert.Equal(0, await EventCountAsync(id, EventCode.TenantResumed, ct));
    }
}
