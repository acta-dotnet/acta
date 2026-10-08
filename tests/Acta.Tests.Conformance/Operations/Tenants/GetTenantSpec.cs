using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>
/// Conformance for the tenant point-read: <c>GetTenant</c> returns the <c>ListTenantsItem</c>
/// projection addressed by canonical key or internal id, resolves suspended tenants (lookup is
/// status-blind), and returns null when no row matches.
/// </summary>
[ConformanceSpec(
    "get-tenant.point-read",
    "GetTenant reads a tenant by key and GetTenantKey reads its key by id",
    Area = "Catalog",
    Contract = "GetTenant returns the GetTenantResponse for a known key in any status or null, and GetTenantKey returns that tenant's key for its id."
)]
public sealed class GetTenantSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITenants Tenants() => Services.GetRequiredService<ITenants>();

    private GetTenantHandler GetTenant() => Services.GetRequiredService<GetTenantHandler>();

    private GetTenantKeyHandler GetTenantKey() => Services.GetRequiredService<GetTenantKeyHandler>();

    [Fact(DisplayName = "A known key returns the populated row and its id reads back the key")]
    public async Task Known_key_and_id_return_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("get-ten");
        var id = (await Tenants().RegisterAsync(key, "Acme Corp", "desc", ct)).TenantId;

        var byKey = await GetTenant().HandleAsync(new GetTenantQuery(key), ct);
        var keyById = (await GetTenantKey().HandleAsync(new GetTenantKeyQuery(id), ct))?.TenantKey;

        Assert.NotNull(byKey);
        Assert.Equal(id, byKey!.TenantId);
        Assert.Equal(key, byKey.TenantKey);
        Assert.Equal("Acme Corp", byKey.DisplayName);
        Assert.Equal("desc", byKey.Description);
        Assert.Equal(TenantStatusCode.Active, byKey.Status);
        Assert.Equal(key, keyById);
    }

    [Fact(DisplayName = "A suspended tenant still resolves with status Suspended")]
    public async Task Suspended_tenant_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("get-ten-susp");
        var id = (await Tenants().RegisterAsync(key, null, null, ct)).TenantId;
        await Tenants().SuspendAsync(key, null, null, ct);

        var row = await GetTenant().HandleAsync(new GetTenantQuery(key), ct);

        Assert.NotNull(row);
        Assert.Equal(id, row!.TenantId);
        Assert.Equal(TenantStatusCode.Suspended, row.Status);
    }

    [Fact(DisplayName = "An unknown key returns null")]
    public async Task Unknown_key_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;

        var row = await GetTenant().HandleAsync(new GetTenantQuery(TestKey("get-ten-ghost")), ct);

        Assert.Null(row);
    }
}
