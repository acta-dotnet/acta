using Acta.Modules.Execution.Tenants;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Tenants;

/// <summary>
/// Conformance for the tenant point-read: <c>GetTenant</c> returns the <c>TenantListItem</c>
/// projection addressed by canonical key or internal id, resolves suspended tenants (lookup is
/// status-blind), and returns null when no row matches.
/// </summary>
[ConformanceSpec(
    "get-tenant.point-read",
    "GetTenant returns the tenant for a known key or id and null for an unknown one",
    Area = "Catalog",
    Contract = "GetTenant returns the TenantListItem projection for a matching key or internal id regardless of status and null when no row matches.",
    Arrange = "A tenant is registered and optionally suspended so a known key and id exist.",
    Act = "GetTenant is called by key, by id, and with a key that matches no row.",
    Assert = "The known key and id return the same populated row including its status and the unknown key returns null."
)]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.GetTenantAsync))]
public abstract class GetTenantSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private TenantsService Tenants() => Services.GetRequiredService<TenantsService>();

    private ITenantStore Store() => Services.GetRequiredService<ITenantStore>();

    [Fact(DisplayName = "A known key returns the populated row and by-id returns the same row")]
    public async Task Known_key_and_id_return_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("get-ten");
        var id = await Tenants().RegisterAsync(key, "Acme Corp", "desc", ct);

        var byKey = await Store().GetTenantAsync(new TenantPointLookup(key, null), ct);
        var byId = await Store().GetTenantAsync(new TenantPointLookup(null, id), ct);

        Assert.NotNull(byKey);
        Assert.Equal(id, byKey!.TenantId);
        Assert.Equal(key, byKey.TenantKey);
        Assert.Equal("Acme Corp", byKey.DisplayName);
        Assert.Equal("desc", byKey.Description);
        Assert.Equal(TenantStatusCode.Active, byKey.Status);
        Assert.Equal(byKey, byId);
    }

    [Fact(DisplayName = "A suspended tenant still resolves with status Suspended")]
    public async Task Suspended_tenant_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("get-ten-susp");
        var id = await Tenants().RegisterAsync(key, null, null, ct);
        await Tenants().SuspendAsync(key, null, null, ct);

        var row = await Store().GetTenantAsync(new TenantPointLookup(key, null), ct);

        Assert.NotNull(row);
        Assert.Equal(id, row!.TenantId);
        Assert.Equal(TenantStatusCode.Suspended, row.Status);
    }

    [Fact(DisplayName = "An unknown key returns null")]
    public async Task Unknown_key_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;

        var row = await Store().GetTenantAsync(new TenantPointLookup(TestKey("get-ten-ghost"), null), ct);

        Assert.Null(row);
    }
}
