using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>
/// Conformance for the tenant point-read: <c>GetTenant</c> returns the <c>ListTenantsItem</c>
/// projection addressed by canonical name or internal id, resolves suspended tenants (lookup is
/// status-blind), and returns null when no row matches.
/// </summary>
[ConformanceSpec(
    "get-tenant.point-read",
    "GetTenant reads a tenant by name and GetTenantName reads its name by id",
    Area = "Catalog",
    Contract = "GetTenant returns the GetTenantResponse for a known name in any status or null, and GetTenantName returns that tenant's name for its id."
)]
public sealed class GetTenantSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITenants Tenants() => Services.GetRequiredService<ITenants>();

    private GetTenantHandler GetTenant() => Services.GetRequiredService<GetTenantHandler>();

    private GetTenantNameHandler GetTenantName() => Services.GetRequiredService<GetTenantNameHandler>();

    [Fact(DisplayName = "A known name returns the populated row and its id reads back the name")]
    public async Task Known_key_and_id_return_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("get-ten");
        var id = (await Tenants().RegisterAsync(key, "Acme Corp", "desc", ct)).TenantId;

        var byKey = await GetTenant().HandleAsync(new GetTenantQuery(key), ct);
        var keyById = (await GetTenantName().HandleAsync(new GetTenantNameQuery(id), ct))?.TenantName;

        Assert.NotNull(byKey);
        Assert.Equal(id, byKey!.TenantId);
        Assert.Equal(key, byKey.TenantName);
        Assert.Equal("Acme Corp", byKey.DisplayName);
        Assert.Equal("desc", byKey.Description);
        Assert.Equal(TenantStatusCode.Active, byKey.Status);
        Assert.Equal(key, keyById);
    }

    [Fact(DisplayName = "A suspended tenant still resolves with status Suspended")]
    public async Task Suspended_tenant_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("get-ten-susp");
        var id = (await Tenants().RegisterAsync(key, null, null, ct)).TenantId;
        await Tenants().SuspendAsync(key, null, null, ct);

        var row = await GetTenant().HandleAsync(new GetTenantQuery(key), ct);

        Assert.NotNull(row);
        Assert.Equal(id, row!.TenantId);
        Assert.Equal(TenantStatusCode.Suspended, row.Status);
    }

    [Fact(DisplayName = "An unknown name returns null")]
    public async Task Unknown_key_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;

        var row = await GetTenant().HandleAsync(new GetTenantQuery(Unique("get-ten-ghost")), ct);

        Assert.Null(row);
    }
}
