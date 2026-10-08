using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Tenants;

/// <summary>Conformance for the engine's id-to-key read: the stored, normalized key of any tenant, and null for an id no tenant has.</summary>
[ConformanceSpec(
    "get-tenant-key.by-id",
    "A tenant id resolves to its stored key",
    Area = "Admin",
    Contract = "GetTenantKey returns the normalized key of the tenant with the given id whatever its status, and null when no tenant has that id."
)]
public sealed class GetTenantKeySpec : ActaStorageTestBase<ConformanceFixture>
{
    private ITenants Tenants => Services.GetRequiredService<ITenants>();

    private async Task<string?> ReadAsync(int tenantId, CancellationToken ct) =>
        (await Services.GetRequiredService<GetTenantKeyHandler>().HandleAsync(new GetTenantKeyQuery(tenantId), ct))?.TenantKey;

    [Fact(DisplayName = "A tenant registered with a mixed-case key reads back its lowercase key")]
    public async Task Id_resolves_to_the_normalized_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("Key-Acme");
        var id = (await Tenants.RegisterAsync(key, null, null, ct)).TenantId;

        Assert.Equal(key.ToLowerInvariant(), await ReadAsync(id, ct));
    }

    [Fact(DisplayName = "A suspended tenant still resolves its key")]
    public async Task Suspended_tenant_still_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("key-held");
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
