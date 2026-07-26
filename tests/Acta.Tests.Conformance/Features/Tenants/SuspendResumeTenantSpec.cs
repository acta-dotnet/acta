using Acta;
using Acta.Features.Jobs;
using Acta.Features.Namespaces;
using Acta.Features.Shared;
using Acta.Features.Tenants;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Tenants;

/// <summary>Conformance for tenant suspend/resume: idempotent status flip, version bump, one 15xx event to the sys namespace, and NotFound for unknown keys.</summary>
[ConformanceSpec(
    "suspend-resume-tenant.status-flip",
    "Tenant suspend and resume flip status and emit one 15xx event to sys namespace",
    Area = "Admin",
    Contract = "Suspend and resume flip tenant status with a version bump, emit tenant.suspended/tenant.resumed to sys namespace 1, and report NotFound for unknown keys.",
    Arrange = "An active tenant is registered.",
    Act = "The tenant is suspended, suspended again, resumed, resumed again, and an unknown key is attempted.",
    Assert = "The first suspend/resume apply with a bumped version and one event each, repeats report AlreadyInState with no new event, and unknown keys report NotFound."
)]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.SuspendTenantAsync))]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.ResumeTenantAsync))]
public abstract class SuspendResumeTenantSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static JobControlActor Actor() => new(JobActorCode.Operator, "op-1");

    private async Task<Tenant?> ReadTenantAsync(string key, CancellationToken ct) =>
        await Db.From<Tenant>().Where(t => t.TenantKey == key).SingleOrDefaultAsync(ct);

    private async Task<int> EventCountAsync(int tenantId, JobEventCode code, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.TenantId == tenantId && e.EventCode == code).CountAsync(ct);

    [Fact(DisplayName = "Suspending an active tenant applies, bumps version, and emits one tenant.suspended to sys")]
    public async Task Suspend_applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-suspend");
        var id = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, ct);
        var before = await ReadTenantAsync(key, ct);

        var outcome = await Services
            .GetRequiredService<ITenantStore>()
            .SuspendTenantAsync(new TenantControlCommand(key, Actor(), "hold"), ct);

        Assert.Equal(AdminControlAction.Applied, outcome.Action);
        var after = await ReadTenantAsync(key, ct);
        Assert.Equal(TenantStatusCode.Suspended, after!.Status);
        Assert.True(after.Version > before!.Version);
        Assert.Equal(after.Version, outcome.Version);
        Assert.Equal(1, await EventCountAsync(id, JobEventCode.TenantSuspended, ct));
        var ev = await Db.From<JobEvent>()
            .Where(e => e.TenantId == id && e.EventCode == JobEventCode.TenantSuspended)
            .SingleOrDefaultAsync(ct);
        Assert.Equal((short)1, ev!.NamespaceId);
        Assert.Equal("op-1", ev.ActorKey);
        Assert.Equal("hold", ev.ReasonMessage);
    }

    [Fact(DisplayName = "Re-suspending is AlreadyInState with no second event")]
    public async Task Suspend_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-suspend-idem");
        var id = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, ct);
        await Services.GetRequiredService<ITenantStore>().SuspendTenantAsync(new TenantControlCommand(key, Actor(), null), ct);

        var again = await Services.GetRequiredService<ITenantStore>().SuspendTenantAsync(new TenantControlCommand(key, Actor(), null), ct);

        Assert.Equal(AdminControlAction.AlreadyInState, again.Action);
        Assert.Equal(1, await EventCountAsync(id, JobEventCode.TenantSuspended, ct));
    }

    [Fact(DisplayName = "Resuming a suspended tenant applies and emits one tenant.resumed")]
    public async Task Resume_applies_and_emits()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-resume");
        var id = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, ct);
        await Services.GetRequiredService<ITenantStore>().SuspendTenantAsync(new TenantControlCommand(key, Actor(), null), ct);

        var outcome = await Services
            .GetRequiredService<ITenantStore>()
            .ResumeTenantAsync(new TenantControlCommand(key, Actor(), "back"), ct);

        Assert.Equal(AdminControlAction.Applied, outcome.Action);
        Assert.Equal(TenantStatusCode.Active, (await ReadTenantAsync(key, ct))!.Status);
        Assert.Equal(1, await EventCountAsync(id, JobEventCode.TenantResumed, ct));
    }

    [Fact(DisplayName = "Re-resuming an active tenant is AlreadyInState with no event")]
    public async Task Resume_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-resume-idem");
        var id = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, ct);

        var outcome = await Services.GetRequiredService<ITenantStore>().ResumeTenantAsync(new TenantControlCommand(key, Actor(), null), ct);

        Assert.Equal(AdminControlAction.AlreadyInState, outcome.Action);
        Assert.Equal(0, await EventCountAsync(id, JobEventCode.TenantResumed, ct));
    }

    [Fact(DisplayName = "Suspending an unknown key is NotFound")]
    public async Task Unknown_key_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await Services
            .GetRequiredService<ITenantStore>()
            .SuspendTenantAsync(new TenantControlCommand(TestKey("adm-ghost"), Actor(), null), ct);
        Assert.Equal(AdminControlAction.NotFound, outcome.Action);
        Assert.Null(outcome.Version);
    }
}
