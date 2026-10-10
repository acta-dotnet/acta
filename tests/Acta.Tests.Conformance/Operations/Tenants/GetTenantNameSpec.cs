using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>Conformance for the engine's id-to-name read: the stored, normalized name of any tenant, and null for an id no tenant has.</summary>
[ConformanceSpec(
    "get-tenant-name.by-id",
    "A tenant id resolves to its stored name",
    Area = "Admin",
    Contract = "GetTenantName returns the normalized name of the tenant with the given id whatever its status, and null when no tenant has that id."
)]
public sealed class GetTenantNameSpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITenants Tenants => Services.GetRequiredService<ITenants>();

    private async Task<string?> ReadAsync(int tenantId, CancellationToken ct) =>
        (await Services.GetRequiredService<GetTenantNameHandler>().HandleAsync(new GetTenantNameQuery(tenantId), ct))?.TenantName;

    [Fact(DisplayName = "A tenant registered with a mixed-case name reads back its lowercase name")]
    public async Task Id_resolves_to_the_normalized_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("Key-Acme");
        var id = (await Tenants.RegisterAsync(key, null, null, ct)).TenantId;

        Assert.Equal(key.ToLowerInvariant(), await ReadAsync(id, ct));
    }

    [Fact(DisplayName = "A suspended tenant still resolves its name")]
    public async Task Suspended_tenant_still_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("key-held");
        var id = (await Tenants.RegisterAsync(key, null, null, ct)).TenantId;
        Assert.Equal(ControlAction.Applied, (await Tenants.SuspendAsync(key, "hold", "op-1", ct)).Action);

        Assert.Equal(key, await ReadAsync(id, ct));
    }

    [Fact(DisplayName = "An id no tenant has reads null")]
    public async Task Unknown_id_reads_null()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await ReadAsync(int.MaxValue, ct));
    }
}
